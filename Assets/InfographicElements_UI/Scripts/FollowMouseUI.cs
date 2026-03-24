using UnityEngine;

public class FollowMouseUI : MonoBehaviour
{
    [SerializeField] private float delaySpeed = 10f;
    [SerializeField] private Canvas parentCanvas; // 대상 UI가 속한 캔버스

    private RectTransform rectTransform;

    void Awake()
    {
        rectTransform = GetComponent<RectTransform>();

        // 만약 인스펙터에서 할당 안 했다면 자동으로 찾기
        if (parentCanvas == null)
            parentCanvas = GetComponentInParent<Canvas>();
    }

    void Update()
    {
        Vector2 localPoint;
        Vector2 screenPoint = Input.mousePosition;


        // 마우스 스크린 좌표를 UI 로컬 좌표로 변환
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            parentCanvas.transform as RectTransform,
            screenPoint,
            parentCanvas.worldCamera,
            out localPoint
        );

        // 기존 위치에서 마우스 방향으로 최대 50픽셀까지만 움직이게 제한하는 예시
        Vector2 targetPos = localPoint * 0.05f; // 감도를 낮춤
        rectTransform.anchoredPosition = Vector2.Lerp(rectTransform.anchoredPosition, targetPos, Time.deltaTime * delaySpeed);

        // Lerp를 이용해 부드럽게 이동 (anchoredPosition 사용)
        //rectTransform.anchoredPosition = Vector2.Lerp(
        //rectTransform.anchoredPosition,
        //localPoint,
        //Time.deltaTime * delaySpeed
        //);
    }
}