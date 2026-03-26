using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class VRKeyboard : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject keyButtonPrefab;
    [SerializeField] private Transform lettersContainer;
    [SerializeField] private Transform numbersContainer;
    [SerializeField] private Transform actionsContainer;
    [SerializeField] private MyDebugLogger logger;

    private TMP_InputField targetField;

    private readonly string[] letters = {
        "Q","W","E","R","T","Y","U","I","O","P",
        "A","S","D","F","G","H","J","K","L",
        "Z","X","C","V","B","N","M"
    };

    private readonly string[] numbers = {
        "1","2","3","4","5","6","7","8","9","0"
    };

    void Awake()
    {
        GenerateKeys(letters, lettersContainer);
        GenerateKeys(numbers, numbersContainer);
        GenerateActionKeys();
        logger.AddLog("Letters generated: " + lettersContainer.childCount);
        logger.AddLog("Numbers generated: " + numbersContainer.childCount);
        logger.AddLog("Actions generated: " + actionsContainer.childCount);
    }

    void Start()
    {
        gameObject.SetActive(false);
    }

    private void GenerateKeys(string[] keys, Transform container)
    {
        foreach (string key in keys)
        {
            GameObject btn = Instantiate(keyButtonPrefab, container);
            
            TMP_Text text = btn.GetComponentInChildren<TMP_Text>();
            Button button = btn.GetComponent<Button>();
            
            // if (text == null) { logger.AddLog("TMP_Text null for key: " + key); return; }
            // if (button == null) { logger.AddLog("Button null for key: " + key); return; }
            
            text.text = key;
            string captured = key;
            button.onClick.AddListener(() => OnKeyPress(captured));
        }
    }

    private void GenerateActionKeys()
    {
        // Space
        GameObject space = Instantiate(keyButtonPrefab, actionsContainer);
        space.GetComponentInChildren<TMP_Text>().text = "Space";
        space.GetComponent<Button>().onClick.AddListener(OnSpace);

        // Backspace
        GameObject backspace = Instantiate(keyButtonPrefab, actionsContainer);
        backspace.GetComponentInChildren<TMP_Text>().text = "Back";
        backspace.GetComponent<Button>().onClick.AddListener(OnBackspace);

        // Clear
        GameObject clear = Instantiate(keyButtonPrefab, actionsContainer);
        clear.GetComponentInChildren<TMP_Text>().text = "Clear";
        clear.GetComponent<Button>().onClick.AddListener(OnClear);

        GameObject close = Instantiate(keyButtonPrefab, actionsContainer);
        close.GetComponentInChildren<TMP_Text>().text = "Close";
        close.GetComponent<Button>().onClick.AddListener(OnClose);
    }

    public void Show(TMP_InputField field)
    {
        logger.AddLog("Show called. Letters: " + lettersContainer.childCount);
        targetField = field;
        gameObject.SetActive(true);
    }

    public void Hide()
    {
        targetField = null;
        gameObject.SetActive(false);
    }

    private void OnKeyPress(string key)
    {
        if (targetField == null) return;
        targetField.text += key;
    }

    private void OnBackspace()
    {
        if (targetField == null || targetField.text.Length == 0) return;
        targetField.text = targetField.text[..^1];
    }

    private void OnSpace()
    {
        if (targetField == null) return;
        targetField.text += " ";
    }

    private void OnClear()
    {
        if (targetField == null) return;
        targetField.text = "";
    }

    private void OnClose()
    {
        Hide();
    }
}