using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace Core.Multiplayer
{
    public enum MultiplayerSessionState
    {
        Idle,
        CreatingHostSession,
        JoiningClientSession,
        LobbyActive,
        StartingGameplay,
        Closing,
        Closed
    }

    public enum MultiplayerSessionFailureKind
    {
        None,
        WrongJoinCode,
        Fatal
    }

    public readonly struct MultiplayerSessionSnapshot
    {
        public MultiplayerSessionSnapshot(bool hasActiveSession, string roomTitle, string joinCode, int connectedPlayerCount, string lobbyStatusText, bool isHost, bool canStart)
        {
            HasActiveSession = hasActiveSession;
            RoomTitle = roomTitle ?? string.Empty;
            JoinCode = joinCode ?? string.Empty;
            ConnectedPlayerCount = connectedPlayerCount;
            LobbyStatusText = lobbyStatusText ?? string.Empty;
            IsHost = isHost;
            CanStart = canStart;
        }

        public bool HasActiveSession { get; }
        public string RoomTitle { get; }
        public string JoinCode { get; }
        public int ConnectedPlayerCount { get; }
        public string LobbyStatusText { get; }
        public bool IsHost { get; }
        public bool CanStart { get; }
    }

    [DisallowMultipleComponent]
    public sealed class MultiplayerSessionService : MonoBehaviour
    {
        private const string RelayConnectionType = "dtls";
        private const string RelayJoinCodeDataKey = "RelayJoinCode";
        private const string SessionStateDataKey = "SessionState";
        private const string ModeDataKey = "Mode";
        private const string WaitingStateValue = "Waiting";
        private const string BossRaidCoopModeValue = "BossRaidCoop";
        private const string WrongKeyMessage = "Wrong key. Please type again.";
        private const float LobbyHeartbeatIntervalSeconds = 15f;

        private static MultiplayerSessionService _instance;

        private Task _currentOperationTask;
        private Task _refreshLobbyTask;
        private MultiplayerRuntimeRoot _runtimeRoot;
        private MultiplayerSessionState _state = MultiplayerSessionState.Idle;
        private MultiplayerSessionSnapshot _currentSnapshot;
        private Lobby _currentLobby;
#if UGS_BETA_LOBBY_EVENTS && UGS_LOBBY_EVENTS
        private ILobbyEvents _lobbyEvents;
#endif
        private string _localPlayerId = string.Empty;
        private string _lastErrorMessage = string.Empty;
        private MultiplayerSessionFailureKind _lastFailureKind = MultiplayerSessionFailureKind.None;
        private bool _isHost;
        private bool _heartbeatEnabled;
        private bool _heartbeatRequestInFlight;
        private float _heartbeatTimer;

        public static bool HasInstance => _instance != null;
        public static MultiplayerSessionService Instance => GetOrCreateInstance();

        public MultiplayerSessionState State => _state;
        public MultiplayerSessionSnapshot CurrentSnapshot => _currentSnapshot;
        public string LastErrorMessage => _lastErrorMessage;
        public MultiplayerSessionFailureKind LastFailureKind => _lastFailureKind;
        public bool IsBusy => _currentOperationTask != null && !_currentOperationTask.IsCompleted;
        public bool HasActiveSession => _currentSnapshot.HasActiveSession || IsNetworkSessionAlive();

        public event Action<MultiplayerSessionState> StateChanged;
        public event Action<MultiplayerSessionSnapshot> SnapshotChanged;
        public event Action<string> FatalErrorOccurred;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ResetStaticState()
        {
            _instance = null;
        }

        private static MultiplayerSessionService GetOrCreateInstance()
        {
            if (_instance != null)
            {
                return _instance;
            }

            GameObject host = new GameObject("MultiplayerSessionService");
            _instance = host.AddComponent<MultiplayerSessionService>();
            return _instance;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if (!_heartbeatEnabled || _heartbeatRequestInFlight || string.IsNullOrEmpty(_currentLobby?.Id) || _state != MultiplayerSessionState.LobbyActive)
            {
                return;
            }

            _heartbeatTimer += Time.deltaTime;
            if (_heartbeatTimer < LobbyHeartbeatIntervalSeconds)
            {
                return;
            }

            _heartbeatTimer = 0f;
            SendLobbyHeartbeatAsync();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private sealed class WrongJoinCodeException : Exception
        {
            public WrongJoinCodeException() : base(WrongKeyMessage)
            {
            }
        }

        public Task CreateHostSessionAsync(string roomTitle)
        {
            if (IsBusy)
            {
                throw new InvalidOperationException("Multiplayer session service is busy.");
            }

            if (HasActiveSession)
            {
                throw new InvalidOperationException("A multiplayer session is already active.");
            }

            _currentOperationTask = CreateHostSessionInternalAsync(roomTitle);
            return _currentOperationTask;
        }

        public Task JoinClientSessionAsync(string joinCode)
        {
            if (IsBusy)
            {
                throw new InvalidOperationException("Multiplayer session service is busy.");
            }

            if (HasActiveSession)
            {
                throw new InvalidOperationException("A multiplayer session is already active.");
            }

            _currentOperationTask = JoinClientSessionInternalAsync(joinCode);
            return _currentOperationTask;
        }

        public Task ShutdownSessionAsync()
        {
            if (IsBusy)
            {
                return _currentOperationTask;
            }

            _lastErrorMessage = string.Empty;
            _lastFailureKind = MultiplayerSessionFailureKind.None;
            _currentOperationTask = ShutdownSessionInternalAsync();
            return _currentOperationTask;
        }

        private async Task CreateHostSessionInternalAsync(string roomTitle)
        {
            _lastErrorMessage = string.Empty;
            _lastFailureKind = MultiplayerSessionFailureKind.None;
            SetState(MultiplayerSessionState.CreatingHostSession);

            try
            {
                await MultiplayerServicesBootstrap.Instance.EnsureInitializedAsync();

                _localPlayerId = MultiplayerServicesBootstrap.Instance.PlayerId ?? string.Empty;
                if (string.IsNullOrEmpty(_localPlayerId))
                {
                    throw new InvalidOperationException("PlayerId is empty before Host create.");
                }

                _runtimeRoot = MultiplayerRuntimeRoot.Instance;
                _runtimeRoot.EnsureConfigured();
                EnsureNetworkManagerIsIdle(_runtimeRoot.NetworkManager);

                Debug.Log("MultiplayerSessionService: Creating Relay allocation for Host.");
                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(1);

                Debug.Log("MultiplayerSessionService: Requesting Relay join code for Host.");
                string relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

                Debug.Log("MultiplayerSessionService: Creating Lobby for Host.");
                CreateLobbyOptions options = BuildHostCreateOptions(relayJoinCode);
                Lobby lobby = await LobbyService.Instance.CreateLobbyAsync(roomTitle, 2, options);

                RelayServerEndpoint relayEndpoint = ResolveRelayEndpoint(allocation);
                _runtimeRoot.UnityTransport.SetHostRelayData(
                    relayEndpoint.Host,
                    (ushort)relayEndpoint.Port,
                    allocation.AllocationIdBytes,
                    allocation.Key,
                    allocation.ConnectionData,
                    string.Equals(relayEndpoint.ConnectionType, RelayConnectionType, StringComparison.OrdinalIgnoreCase) || relayEndpoint.Secure);
                if (!_runtimeRoot.NetworkManager.StartHost())
                {
                    throw new InvalidOperationException("NGO Host failed to start.");
                }

                RegisterNetworkCallbacks();

                _isHost = true;
                _currentLobby = lobby;
                _heartbeatEnabled = true;
                _heartbeatRequestInFlight = false;
                _heartbeatTimer = 0f;

                await SubscribeToLobbyEventsAsync(lobby.Id);

                SetState(MultiplayerSessionState.LobbyActive);
                PublishSnapshot(BuildCurrentSnapshot());

                Debug.Log($"MultiplayerSessionService: Host session started. LobbyId={lobby.Id}, JoinCode={relayJoinCode}");
            }
            catch (Exception ex)
            {
                _lastFailureKind = MultiplayerSessionFailureKind.Fatal;
                _lastErrorMessage = $"Host create failed: {ex.Message}";
                Debug.LogError($"MultiplayerSessionService: {_lastErrorMessage}");
                await ShutdownSessionInternalAsync();
                throw;
            }
            finally
            {
                _currentOperationTask = null;
            }
        }

        private async Task JoinClientSessionInternalAsync(string rawJoinCode)
        {
            _lastErrorMessage = string.Empty;
            _lastFailureKind = MultiplayerSessionFailureKind.None;
            SetState(MultiplayerSessionState.JoiningClientSession);

            try
            {
                await MultiplayerServicesBootstrap.Instance.EnsureInitializedAsync();

                _localPlayerId = MultiplayerServicesBootstrap.Instance.PlayerId ?? string.Empty;
                if (string.IsNullOrEmpty(_localPlayerId))
                {
                    throw new InvalidOperationException("PlayerId is empty before Client join.");
                }

                string joinCode = NormalizeJoinCode(rawJoinCode);
                if (!IsValidJoinCode(joinCode))
                {
                    throw new WrongJoinCodeException();
                }

                _runtimeRoot = MultiplayerRuntimeRoot.Instance;
                _runtimeRoot.EnsureConfigured();
                EnsureNetworkManagerIsIdle(_runtimeRoot.NetworkManager);

                Debug.Log($"MultiplayerSessionService: Querying Lobby for Client join. JoinCode={joinCode}");
                Lobby lobby = await QueryJoinableLobbyByJoinCodeAsync(joinCode);
                if (lobby == null)
                {
                    throw new WrongJoinCodeException();
                }

                Debug.Log($"MultiplayerSessionService: Joining Lobby {lobby.Id} as Client.");
                _currentLobby = await LobbyService.Instance.JoinLobbyByIdAsync(
                    lobby.Id,
                    new JoinLobbyByIdOptions
                    {
                        Player = new Unity.Services.Lobbies.Models.Player(id: _localPlayerId)
                    });

                Debug.Log($"MultiplayerSessionService: Joining Relay allocation as Client. JoinCode={joinCode}");
                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

                RelayServerEndpoint relayEndpoint = ResolveRelayEndpoint(joinAllocation);
                _runtimeRoot.UnityTransport.SetClientRelayData(
                    relayEndpoint.Host,
                    (ushort)relayEndpoint.Port,
                    joinAllocation.AllocationIdBytes,
                    joinAllocation.Key,
                    joinAllocation.ConnectionData,
                    joinAllocation.HostConnectionData,
                    string.Equals(relayEndpoint.ConnectionType, RelayConnectionType, StringComparison.OrdinalIgnoreCase) || relayEndpoint.Secure);

                RegisterNetworkCallbacks();

                if (!_runtimeRoot.NetworkManager.StartClient())
                {
                    throw new InvalidOperationException("NGO Client failed to start.");
                }

                _isHost = false;
                _heartbeatEnabled = false;
                _heartbeatRequestInFlight = false;
                _heartbeatTimer = 0f;

                await SubscribeToLobbyEventsAsync(_currentLobby.Id);

                SetState(MultiplayerSessionState.LobbyActive);
                PublishSnapshot(BuildCurrentSnapshot());

                Debug.Log($"MultiplayerSessionService: Client session started. LobbyId={_currentLobby.Id}, JoinCode={joinCode}");
            }
            catch (Exception ex)
            {
                if (IsWrongJoinCodeFailure(ex))
                {
                    _lastFailureKind = MultiplayerSessionFailureKind.WrongJoinCode;
                    _lastErrorMessage = WrongKeyMessage;
                    Debug.LogWarning($"MultiplayerSessionService: Client join rejected. {ex.Message}");
                }
                else
                {
                    _lastFailureKind = MultiplayerSessionFailureKind.Fatal;
                    _lastErrorMessage = $"Client join failed: {ex.Message}";
                    Debug.LogError($"MultiplayerSessionService: {_lastErrorMessage}");
                }

                await ShutdownSessionInternalAsync();
                throw;
            }
            finally
            {
                _currentOperationTask = null;
            }
        }

        private CreateLobbyOptions BuildHostCreateOptions(string relayJoinCode)
        {
            return new CreateLobbyOptions
            {
                IsPrivate = false,
                Player = new Unity.Services.Lobbies.Models.Player(id: _localPlayerId),
                Data = new Dictionary<string, DataObject>
                {
                    {
                        RelayJoinCodeDataKey,
                        new DataObject(DataObject.VisibilityOptions.Public, relayJoinCode, DataObject.IndexOptions.S1)
                    },
                    {
                        SessionStateDataKey,
                        new DataObject(DataObject.VisibilityOptions.Public, WaitingStateValue)
                    },
                    {
                        ModeDataKey,
                        new DataObject(DataObject.VisibilityOptions.Public, BossRaidCoopModeValue)
                    }
                }
            };
        }

        private async Task<Lobby> QueryJoinableLobbyByJoinCodeAsync(string joinCode)
        {
            QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(
                new QueryLobbiesOptions
                {
                    Count = 1,
                    Filters = new List<QueryFilter>
                    {
                        new QueryFilter(QueryFilter.FieldOptions.S1, joinCode, QueryFilter.OpOptions.EQ),
                        new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT)
                    }
                });

            return response != null && response.Results != null && response.Results.Count > 0
                ? response.Results[0]
                : null;
        }

        private static RelayServerEndpoint ResolveRelayEndpoint(Allocation allocation)
        {
            if (allocation == null || allocation.ServerEndpoints == null)
            {
                throw new InvalidOperationException("Relay allocation did not include any server endpoints.");
            }

            for (int i = 0; i < allocation.ServerEndpoints.Count; i++)
            {
                RelayServerEndpoint endpoint = allocation.ServerEndpoints[i];
                if (endpoint != null && string.Equals(endpoint.ConnectionType, RelayConnectionType, StringComparison.OrdinalIgnoreCase))
                {
                    return endpoint;
                }
            }

            throw new InvalidOperationException($"Relay allocation does not include a {RelayConnectionType} endpoint.");
        }

        private static RelayServerEndpoint ResolveRelayEndpoint(JoinAllocation allocation)
        {
            if (allocation == null || allocation.ServerEndpoints == null)
            {
                throw new InvalidOperationException("Relay join allocation did not include any server endpoints.");
            }

            for (int i = 0; i < allocation.ServerEndpoints.Count; i++)
            {
                RelayServerEndpoint endpoint = allocation.ServerEndpoints[i];
                if (endpoint != null && string.Equals(endpoint.ConnectionType, RelayConnectionType, StringComparison.OrdinalIgnoreCase))
                {
                    return endpoint;
                }
            }

            throw new InvalidOperationException($"Relay join allocation does not include a {RelayConnectionType} endpoint.");
        }

        private Task SubscribeToLobbyEventsAsync(string lobbyId)
        {
#if UGS_BETA_LOBBY_EVENTS && UGS_LOBBY_EVENTS
            return SubscribeToLobbyEventsInternalAsync(lobbyId);
#else
            return Task.CompletedTask;
#endif
        }

#if UGS_BETA_LOBBY_EVENTS && UGS_LOBBY_EVENTS
        private async Task SubscribeToLobbyEventsInternalAsync(string lobbyId)
        {
            if (string.IsNullOrEmpty(lobbyId))
            {
                return;
            }

            LobbyEventCallbacks callbacks = new LobbyEventCallbacks();
            callbacks.PlayerJoined += _ => RequestLobbyRefresh();
            callbacks.PlayerLeft += _ => RequestLobbyRefresh();
            callbacks.DataChanged += _ => RequestLobbyRefresh();
            callbacks.DataAdded += _ => RequestLobbyRefresh();
            callbacks.DataRemoved += _ => RequestLobbyRefresh();
            callbacks.LobbyDeleted += () => BeginFatalShutdown("Lobby closed. Returning to title.");
            callbacks.KickedFromLobby += () => BeginFatalShutdown("Disconnected from lobby.");
            callbacks.LobbyEventConnectionStateChanged += state =>
            {
                if (state == LobbyEventConnectionState.Error)
                {
                    Debug.LogWarning("MultiplayerSessionService: Lobby event connection entered Error state.");
                }
            };

            _lobbyEvents = await LobbyService.Instance.SubscribeToLobbyEventsAsync(lobbyId, callbacks);
        }
#endif

        private void RequestLobbyRefresh()
        {
            if (_state != MultiplayerSessionState.LobbyActive || string.IsNullOrEmpty(_currentLobby?.Id))
            {
                return;
            }

            if (_refreshLobbyTask != null && !_refreshLobbyTask.IsCompleted)
            {
                return;
            }

            _refreshLobbyTask = RefreshLobbyAsync();
        }

        private async Task RefreshLobbyAsync()
        {
            try
            {
                _currentLobby = await LobbyService.Instance.GetLobbyAsync(_currentLobby.Id);
                PublishSnapshot(BuildCurrentSnapshot());
            }
            catch (Exception ex)
            {
                if (_state == MultiplayerSessionState.Closing)
                {
                    return;
                }

                Debug.LogWarning($"MultiplayerSessionService: Lobby refresh failed. {ex.Message}");
            }
            finally
            {
                _refreshLobbyTask = null;
            }
        }

        private void RegisterNetworkCallbacks()
        {
            if (_runtimeRoot == null || _runtimeRoot.NetworkManager == null)
            {
                return;
            }

            _runtimeRoot.NetworkManager.OnClientConnectedCallback -= HandleNetworkClientConnected;
            _runtimeRoot.NetworkManager.OnClientDisconnectCallback -= HandleNetworkClientDisconnected;
            _runtimeRoot.NetworkManager.OnClientConnectedCallback += HandleNetworkClientConnected;
            _runtimeRoot.NetworkManager.OnClientDisconnectCallback += HandleNetworkClientDisconnected;
        }

        private void UnregisterNetworkCallbacks()
        {
            if (_runtimeRoot == null || _runtimeRoot.NetworkManager == null)
            {
                return;
            }

            _runtimeRoot.NetworkManager.OnClientConnectedCallback -= HandleNetworkClientConnected;
            _runtimeRoot.NetworkManager.OnClientDisconnectCallback -= HandleNetworkClientDisconnected;
        }

        private void HandleNetworkClientConnected(ulong clientId)
        {
            if (_state != MultiplayerSessionState.LobbyActive)
            {
                return;
            }

            PublishSnapshot(BuildCurrentSnapshot());
            if (clientId != NetworkManager.ServerClientId)
            {
                RequestLobbyRefresh();
            }
        }

        private void HandleNetworkClientDisconnected(ulong clientId)
        {
            if (_state != MultiplayerSessionState.LobbyActive)
            {
                return;
            }

            PublishSnapshot(BuildCurrentSnapshot());
            if (clientId != NetworkManager.ServerClientId)
            {
                RequestLobbyRefresh();
            }
        }

        private MultiplayerSessionSnapshot BuildCurrentSnapshot()
        {
            int connectedPlayerCount = ResolveConnectedPlayerCount();
            string roomTitle = _currentLobby != null ? _currentLobby.Name : string.Empty;
            string joinCode = TryGetLobbyDataValue(RelayJoinCodeDataKey);

            string statusText = connectedPlayerCount >= 2
                ? "2/2 connected"
                : "Waiting for other player...";

            return new MultiplayerSessionSnapshot(
                hasActiveSession: !string.IsNullOrEmpty(_currentLobby?.Id),
                roomTitle: roomTitle,
                joinCode: joinCode,
                connectedPlayerCount: connectedPlayerCount,
                lobbyStatusText: statusText,
                isHost: _isHost,
                canStart: false);
        }

        private int ResolveConnectedPlayerCount()
        {
            int lobbyPlayerCount = _currentLobby != null && _currentLobby.Players != null ? _currentLobby.Players.Count : 0;
            int networkPlayerCount = 0;

            if (_runtimeRoot != null && _runtimeRoot.NetworkManager != null && _runtimeRoot.NetworkManager.IsServer)
            {
                networkPlayerCount = _runtimeRoot.NetworkManager.ConnectedClientsIds.Count;
            }

            return Mathf.Max(Mathf.Max(lobbyPlayerCount, networkPlayerCount), _isHost && !string.IsNullOrEmpty(_currentLobby?.Id) ? 1 : 0);
        }

        private string TryGetLobbyDataValue(string key)
        {
            if (_currentLobby == null || _currentLobby.Data == null || string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            return _currentLobby.Data.TryGetValue(key, out DataObject value) && value != null
                ? value.Value ?? string.Empty
                : string.Empty;
        }

        private void PublishSnapshot(MultiplayerSessionSnapshot snapshot)
        {
            _currentSnapshot = snapshot;
            SnapshotChanged?.Invoke(_currentSnapshot);
        }

        private void SetState(MultiplayerSessionState nextState)
        {
            if (_state == nextState)
            {
                return;
            }

            _state = nextState;
            StateChanged?.Invoke(_state);
        }

        private void BeginFatalShutdown(string message)
        {
            if (_state == MultiplayerSessionState.Closing)
            {
                return;
            }

            _lastFailureKind = MultiplayerSessionFailureKind.Fatal;
            _lastErrorMessage = message;
            Debug.LogError($"MultiplayerSessionService: {message}");
            _ = HandleFatalShutdownAsync(message);
        }

        private async Task HandleFatalShutdownAsync(string message)
        {
            await ShutdownSessionInternalAsync();
            FatalErrorOccurred?.Invoke(message);
        }

        private async void SendLobbyHeartbeatAsync()
        {
            if (_heartbeatRequestInFlight || string.IsNullOrEmpty(_currentLobby?.Id))
            {
                return;
            }

            _heartbeatRequestInFlight = true;

            try
            {
                await LobbyService.Instance.SendHeartbeatPingAsync(_currentLobby.Id);
                Debug.Log("MultiplayerSessionService: Lobby heartbeat sent.");
            }
            catch (Exception ex)
            {
                BeginFatalShutdown($"Lobby heartbeat failed: {ex.Message}");
            }
            finally
            {
                _heartbeatRequestInFlight = false;
            }
        }

        private async Task ShutdownSessionInternalAsync()
        {
            SetState(MultiplayerSessionState.Closing);

            _heartbeatEnabled = false;
            _heartbeatTimer = 0f;

            await UnsubscribeLobbyEventsSafeAsync();
            await DeleteOrLeaveLobbySafeAsync();
            await ShutdownNetworkSafeAsync();

            UnregisterNetworkCallbacks();
            ClearCachedSessionState();
            PublishSnapshot(default);
            SetState(MultiplayerSessionState.Closed);

            Debug.Log("MultiplayerSessionService: Cleanup complete.");
            _currentOperationTask = null;
        }

        private Task UnsubscribeLobbyEventsSafeAsync()
        {
#if UGS_BETA_LOBBY_EVENTS && UGS_LOBBY_EVENTS
            return UnsubscribeLobbyEventsSafeInternalAsync();
#else
            return Task.CompletedTask;
#endif
        }

#if UGS_BETA_LOBBY_EVENTS && UGS_LOBBY_EVENTS
        private async Task UnsubscribeLobbyEventsSafeInternalAsync()
        {
            if (_lobbyEvents == null)
            {
                return;
            }

            try
            {
                await _lobbyEvents.UnsubscribeAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"MultiplayerSessionService: Lobby event unsubscribe failed. {ex.Message}");
            }
            finally
            {
                _lobbyEvents = null;
            }
        }
#endif

        private async Task DeleteOrLeaveLobbySafeAsync()
        {
            if (string.IsNullOrEmpty(_currentLobby?.Id))
            {
                return;
            }

            try
            {
                if (_isHost)
                {
                    Debug.Log($"MultiplayerSessionService: Deleting lobby {_currentLobby.Id}.");
                    await LobbyService.Instance.DeleteLobbyAsync(_currentLobby.Id);
                }
                else if (!string.IsNullOrEmpty(_localPlayerId))
                {
                    Debug.Log($"MultiplayerSessionService: Leaving lobby {_currentLobby.Id} as player {_localPlayerId}.");
                    await LobbyService.Instance.RemovePlayerAsync(_currentLobby.Id, _localPlayerId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"MultiplayerSessionService: Lobby cleanup failed. {ex.Message}");
            }
        }

        private async Task ShutdownNetworkSafeAsync()
        {
            if (_runtimeRoot == null || _runtimeRoot.NetworkManager == null)
            {
                return;
            }

            NetworkManager networkManager = _runtimeRoot.NetworkManager;
            if (!networkManager.IsServer && !networkManager.IsClient && !networkManager.ShutdownInProgress)
            {
                return;
            }

            networkManager.Shutdown();

            int guardFrames = 120;
            while (guardFrames-- > 0 && (networkManager.IsServer || networkManager.IsClient || networkManager.ShutdownInProgress))
            {
                await Task.Yield();
            }

            if (networkManager.IsServer || networkManager.IsClient || networkManager.ShutdownInProgress)
            {
                Debug.LogWarning("MultiplayerSessionService: NetworkManager shutdown wait timed out.");
            }
        }

        private void ClearCachedSessionState()
        {
            _currentLobby = null;
            _isHost = false;
            _localPlayerId = string.Empty;
            _heartbeatEnabled = false;
            _heartbeatRequestInFlight = false;
        }

        private static string NormalizeJoinCode(string rawJoinCode)
        {
            if (string.IsNullOrWhiteSpace(rawJoinCode))
            {
                return string.Empty;
            }

            return rawJoinCode.Trim().ToUpperInvariant();
        }

        private static bool IsValidJoinCode(string joinCode)
        {
            if (string.IsNullOrWhiteSpace(joinCode) || joinCode.Length != 6)
            {
                return false;
            }

            for (int i = 0; i < joinCode.Length; i++)
            {
                if (!char.IsLetterOrDigit(joinCode[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsWrongJoinCodeFailure(Exception exception)
        {
            if (exception is WrongJoinCodeException)
            {
                return true;
            }

            if (exception is LobbyServiceException lobbyException)
            {
                return lobbyException.Reason == LobbyExceptionReason.EntityNotFound
                       || lobbyException.Reason == LobbyExceptionReason.NoOpenLobbies;
            }

            if (exception is RelayServiceException relayException)
            {
                return relayException.Reason == RelayExceptionReason.InvalidRequest
                       || relayException.Reason == RelayExceptionReason.InvalidArgument
                       || relayException.Reason == RelayExceptionReason.AllocationNotFound
                       || relayException.Reason == RelayExceptionReason.JoinCodeNotFound
                       || relayException.Reason == RelayExceptionReason.EntityNotFound;
            }

            return false;
        }

        private bool IsNetworkSessionAlive()
        {
            return _runtimeRoot != null
                   && _runtimeRoot.NetworkManager != null
                   && (_runtimeRoot.NetworkManager.IsServer || _runtimeRoot.NetworkManager.IsClient || _runtimeRoot.NetworkManager.ShutdownInProgress);
        }

        private static void EnsureNetworkManagerIsIdle(NetworkManager networkManager)
        {
            if (networkManager == null)
            {
                throw new InvalidOperationException("NetworkManager is missing.");
            }

            if (networkManager.IsServer || networkManager.IsClient || networkManager.ShutdownInProgress)
            {
                throw new InvalidOperationException("NetworkManager is already running or shutting down.");
            }
        }
    }
}
