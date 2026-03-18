using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Core.Multiplayer
{
    [DisallowMultipleComponent]
    public sealed class MultiplayerRuntimeRoot : MonoBehaviour
    {
        private static MultiplayerRuntimeRoot _instance;

        public static bool HasInstance => _instance != null;
        public static MultiplayerRuntimeRoot Instance => GetOrCreateInstance();

        public NetworkManager NetworkManager { get; private set; }
        public UnityTransport UnityTransport { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ResetStaticState()
        {
            _instance = null;
        }

        private static MultiplayerRuntimeRoot GetOrCreateInstance()
        {
            if (_instance != null)
            {
                return _instance;
            }

            GameObject host = new GameObject("MultiplayerRuntimeRoot");
            _instance = host.AddComponent<MultiplayerRuntimeRoot>();
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

            EnsureConfigured();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        public void EnsureConfigured()
        {
            EnsureComponents();
            ConfigureNetworkManager();
        }

        private void EnsureComponents()
        {
            UnityTransport = GetComponent<UnityTransport>();
            if (UnityTransport == null)
            {
                UnityTransport = gameObject.AddComponent<UnityTransport>();
            }

            NetworkManager = GetComponent<NetworkManager>();
            if (NetworkManager == null)
            {
                NetworkManager = gameObject.AddComponent<NetworkManager>();
            }
        }

        private void ConfigureNetworkManager()
        {
            if (NetworkManager.NetworkConfig == null)
            {
                NetworkManager.NetworkConfig = new NetworkConfig();
            }

            NetworkManager.NetworkConfig.NetworkTransport = UnityTransport;
            NetworkManager.NetworkConfig.EnableSceneManagement = true;
            NetworkManager.NetworkConfig.PlayerPrefab = null;
            NetworkManager.RunInBackground = true;
        }
    }
}
