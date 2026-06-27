using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Sends chat messages to the Groq chat completions API (OpenAI-compatible)
/// and returns the assistant's reply. Attach this to a GameObject and call
/// <see cref="Send"/> with the player's message.
/// </summary>
public class NpcChat : MonoBehaviour
{
    private const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";

    [Header("Groq API")]
    [Tooltip("Your Groq API key. Treat this as a secret; do not ship it in a public build.")]
    [SerializeField] private string apiKey = "";

    [Tooltip("Model id, e.g. llama-3.3-70b-versatile")]
    [SerializeField] private string model = "llama-3.3-70b-versatile";

    [Header("NPC Behaviour")]
    [Tooltip("System prompt describing who this NPC is and how they should respond.")]
    [TextArea(4, 12)]
    [SerializeField] private string persona =
        "You are a friendly village blacksmith in a fantasy RPG. " +
        "Stay in character, keep replies short (1-3 sentences), and never break the fourth wall.";

    [Header("Generation Settings")]
    [Range(0f, 2f)]
    [SerializeField] private float temperature = 0.7f;

    [Tooltip("Maximum tokens in the model's reply.")]
    [SerializeField] private int maxTokens = 256;

    [Tooltip("Number of recent exchanges to keep as conversation context. 0 disables history.")]
    [SerializeField] private int historyLimit = 10;

    [Header("UI (optional, TextMeshPro)")]
    [Tooltip("Where the player types. If set, pressing Enter sends the message.")]
    [SerializeField] private TMP_InputField inputField;

    [Tooltip("TMP Text that displays the NPC's reply.")]
    [SerializeField] private TMP_Text outputText;

    [Header("Emotion Sprites (judas la carotte)")]
    [Tooltip("2D SpriteRenderer to swap (for a sprite in the scene).")]
    [SerializeField] private SpriteRenderer portrait;

    [Tooltip("UI Image to swap (for a sprite on a Canvas). Optional.")]
    [SerializeField] private Image portraitImage;

    [Tooltip("Map each emotion name to a sprite. The model is told to use only these emotion names.")]
    [SerializeField] private EmotionSprite[] emotionSprites = new EmotionSprite[]
    {
        new EmotionSprite { emotion = "neutral" },
        new EmotionSprite { emotion = "happy" },
        new EmotionSprite { emotion = "sad" },
        new EmotionSprite { emotion = "angry" },
        new EmotionSprite { emotion = "surprised" },
    };

    [Tooltip("Emotion used at start and when the model returns an unknown/empty tag.")]
    [SerializeField] private string defaultEmotion = "neutral";

    [Tooltip("Log emotion detection to the Console to help debug sprite swapping.")]
    [SerializeField] private bool debugEmotions = true;

    [Header("Win / Lose Conditions (described in words)")]
    [Tooltip("Describe, in plain language, when the PLAYER WINS. The NPC judges this after each reply. Leave empty to disable.")]
    [TextArea(2, 6)]
    [SerializeField] private string winCondition =
        "The player wins if they successfully convince you to give them a discount on your wares.";

    [Tooltip("Describe, in plain language, when the PLAYER LOSES. The NPC judges this after each reply. Leave empty to disable.")]
    [TextArea(2, 6)]
    [SerializeField] private string loseCondition =
        "The player loses if they insult you or make you so angry that you refuse to talk to them.";

    [Tooltip("Message shown in the chat log when the player wins.")]
    [SerializeField] private string winMessage = "*** YOU WIN! ***";

    [Tooltip("Message shown in the chat log when the player loses.")]
    [SerializeField] private string loseMessage = "*** GAME OVER - you lost! ***";

    [Tooltip("Invoked once when the player wins. Hook up your Win UI here.")]
    [SerializeField] private UnityEvent onWin;

    [Tooltip("Invoked once when the player loses. Hook up your Game Over UI here.")]
    [SerializeField] private UnityEvent onLose;

    [Header("Lose Condition (automatic, by emotion)")]
    [Tooltip("Extra safety trigger: if the NPC reaches one of these emotions enough times, the player loses. Leave empty to rely only on the described condition above.")]
    [SerializeField] private string[] loseEmotions = { "angry" };

    [Tooltip("How many of these emotions in a row trigger the loss. 1 = lose the first time.")]
    [SerializeField] private int angerThreshold = 3;

    private int _angerStreak;
    private bool _lost;
    private bool _won;

    [Header("On-Screen Chat Box (OnGUI)")]
    [Tooltip("Draw a simple immediate-mode chat box at runtime.")]
    [SerializeField] private bool showChatBox = true;

    [Tooltip("Press this key to show/hide the chat box at runtime. Set to None to disable.")]
    [SerializeField] private KeyCode toggleKey = KeyCode.Tab;

    [Tooltip("Screen rectangle (pixels) for the chat box.")]
    [SerializeField] private Rect chatBoxRect = new Rect(20, 20, 380, 320);

    private readonly List<Message> _history = new List<Message>();

    private readonly List<string> _log = new List<string>();
    private string _input = string.Empty;
    private Vector2 _scroll;
    private bool _waiting;
    private const string SendControlName = "NpcChatInput";

    private void Awake()
    {
        if (inputField != null)
        {
            inputField.onSubmit.AddListener(OnInputSubmit);
        }

        ApplyEmotion(defaultEmotion);
    }

    private void OnDestroy()
    {
        if (inputField != null)
        {
            inputField.onSubmit.RemoveListener(OnInputSubmit);
        }
    }

    private void Update()
    {
        if (WasTogglePressed())
        {
            ToggleChatBox();
        }
    }

    private bool WasTogglePressed()
    {
        if (toggleKey == KeyCode.None)
        {
            return false;
        }

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard == null)
        {
            return false;
        }

        // Most KeyCode names match the Input System Key enum (Tab, F1, Space, A...).
        if (System.Enum.TryParse(toggleKey.ToString(), out UnityEngine.InputSystem.Key key))
        {
            var control = keyboard[key];
            return control != null && control.wasPressedThisFrame;
        }

        return false;
#else
        return Input.GetKeyDown(toggleKey);
#endif
    }

    /// <summary>Show or hide the on-screen chat box.</summary>
    public void SetChatBoxVisible(bool visible)
    {
        showChatBox = visible;
    }

    /// <summary>Flip the on-screen chat box between shown and hidden.</summary>
    public void ToggleChatBox()
    {
        showChatBox = !showChatBox;
    }

    private void OnInputSubmit(string text)
    {
        Ask(text);
        if (inputField != null)
        {
            inputField.text = string.Empty;
            inputField.ActivateInputField();
        }
    }

    /// <summary>
    /// Convenience entry point: sends the message and shows the reply in
    /// <see cref="outputText"/>, the on-screen chat log, and the console.
    /// </summary>
    public void Ask(string userMessage)
    {
        if (IsGameOver)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return;
        }

        AppendLog("You: " + userMessage.Trim());
        _waiting = true;
        Send(userMessage, Show, ShowError);
    }

    private void Show(string reply)
    {
        _waiting = false;

        if (outputText != null)
        {
            outputText.text = reply;
        }

        AppendLog("NPC: " + reply);
        Debug.Log("NPC: " + reply);
    }

    private void ShowError(string error)
    {
        _waiting = false;

        if (outputText != null)
        {
            outputText.text = "[error] " + error;
        }

        AppendLog("[error] " + error);
        Debug.LogError(error);
    }

    private void AppendLog(string line)
    {
        _log.Add(line);
        // Scroll to the bottom on the next OnGUI pass.
        _scroll.y = float.MaxValue;
    }

    private void OnGUI()
    {
        if (!showChatBox)
        {
            return;
        }

        GUILayout.BeginArea(chatBoxRect, GUI.skin.box);

        GUILayout.Label("NPC Chat", EditorStyleLabel());

        // Scrolling conversation log.
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        for (int i = 0; i < _log.Count; i++)
        {
            GUILayout.Label(_log[i], WrappedLabel());
        }
        GUILayout.EndScrollView();

        if (IsGameOver)
        {
            // Game over (win or lose): show a Restart button instead of input.
            if (GUILayout.Button("Restart"))
            {
                ResetGame();
            }

            GUILayout.EndArea();
            return;
        }

        // Input row: text field + Send button.
        GUILayout.BeginHorizontal();

        GUI.SetNextControlName(SendControlName);
        bool enterPressed =
            Event.current.type == EventType.KeyDown &&
            (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
            GUI.GetNameOfFocusedControl() == SendControlName;

        _input = GUILayout.TextField(_input, GUILayout.ExpandWidth(true));

        GUI.enabled = !_waiting;
        bool sendClicked = GUILayout.Button(_waiting ? "..." : "Send", GUILayout.Width(70));
        GUI.enabled = true;

        GUILayout.EndHorizontal();

        if ((sendClicked || enterPressed) && !_waiting && !string.IsNullOrWhiteSpace(_input))
        {
            if (enterPressed)
            {
                Event.current.Use();
            }

            string toSend = _input;
            _input = string.Empty;
            Ask(toSend);
            GUI.FocusControl(SendControlName);
        }

        GUILayout.EndArea();
    }

    private static GUIStyle _wrapped;
    private static GUIStyle WrappedLabel()
    {
        if (_wrapped == null)
        {
            _wrapped = new GUIStyle(GUI.skin.label) { wordWrap = true };
        }
        return _wrapped;
    }

    private static GUIStyle _title;
    private static GUIStyle EditorStyleLabel()
    {
        if (_title == null)
        {
            _title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        }
        return _title;
    }

    /// <summary>
    /// Send a player message to the NPC.
    /// </summary>
    /// <param name="userMessage">The player's text.</param>
    /// <param name="onReply">Called with the assistant's reply on success.</param>
    /// <param name="onError">Called with an error message on failure (optional).</param>
    public void Send(string userMessage, Action<string> onReply, Action<string> onError = null)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            onError?.Invoke("Message is empty.");
            return;
        }

        StartCoroutine(SendRoutine(userMessage, onReply, onError));
    }

    /// <summary>Clears the stored conversation history.</summary>
    public void ResetConversation()
    {
        _history.Clear();
    }

    /// <summary>True once the player has lost.</summary>
    public bool HasLost => _lost;

    /// <summary>True once the player has won.</summary>
    public bool HasWon => _won;

    /// <summary>True once the game has ended (win or lose).</summary>
    public bool IsGameOver => _lost || _won;

    /// <summary>
    /// Resets the game state: clears win/loss, anger streak, and conversation,
    /// re-enables input, and restores the default emotion sprite.
    /// </summary>
    public void ResetGame()
    {
        _lost = false;
        _won = false;
        _angerStreak = 0;
        _history.Clear();

        if (inputField != null)
        {
            inputField.interactable = true;
        }

        ApplyEmotion(defaultEmotion);
    }

    private void CheckLoseCondition(string emotion)
    {
        if (IsGameOver)
        {
            return;
        }

        bool isLoseEmotion = false;
        if (loseEmotions != null)
        {
            foreach (var e in loseEmotions)
            {
                if (string.Equals(e, emotion, StringComparison.OrdinalIgnoreCase))
                {
                    isLoseEmotion = true;
                    break;
                }
            }
        }

        if (isLoseEmotion)
        {
            _angerStreak++;
        }
        else
        {
            _angerStreak = 0;
        }

        if (_angerStreak >= Mathf.Max(1, angerThreshold))
        {
            TriggerLose();
        }
    }

    private void TriggerLose()
    {
        if (IsGameOver)
        {
            return;
        }

        _lost = true;

        if (!string.IsNullOrEmpty(loseMessage))
        {
            AppendLog(loseMessage);
        }

        if (inputField != null)
        {
            inputField.interactable = false;
        }

        if (debugEmotions)
        {
            Debug.Log("[NpcChat] Lose condition triggered.");
        }

        onLose?.Invoke();
    }

    private void TriggerWin()
    {
        if (IsGameOver)
        {
            return;
        }

        _won = true;

        if (!string.IsNullOrEmpty(winMessage))
        {
            AppendLog(winMessage);
        }

        if (inputField != null)
        {
            inputField.interactable = false;
        }

        if (debugEmotions)
        {
            Debug.Log("[NpcChat] Win condition triggered.");
        }

        onWin?.Invoke();
    }

    /// <summary>
    /// Removes any [WIN] / [LOSE] status tags the model appended and reports
    /// the outcome ("win", "lose", or null) it found.
    /// </summary>
    private string ExtractOutcome(string text, out string outcome)
    {
        outcome = null;
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (OutcomeTagPattern.IsMatch(text))
        {
            // A LOSE tag takes precedence if the model somehow emitted both.
            string lower = text.ToLowerInvariant();
            if (lower.Contains("[lose]"))
            {
                outcome = "lose";
            }
            else if (lower.Contains("[win]"))
            {
                outcome = "win";
            }

            text = OutcomeTagPattern.Replace(text, string.Empty).Trim();
        }

        return text;
    }

    private IEnumerator SendRoutine(string userMessage, Action<string> onReply, Action<string> onError)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            onError?.Invoke("Groq API key is not set on the NpcChat component.");
            yield break;
        }

        var messages = new List<Message> { new Message { role = "system", content = BuildSystemPrompt() } };
        messages.AddRange(_history);
        messages.Add(new Message { role = "user", content = userMessage });

        var requestBody = new ChatRequest
        {
            model = model,
            temperature = temperature,
            max_tokens = maxTokens,
            messages = messages.ToArray()
        };

        string json = JsonUtility.ToJson(requestBody);
        byte[] payload = Encoding.UTF8.GetBytes(json);

        using (var request = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(payload);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + apiKey);

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string error = $"Groq request failed ({request.responseCode}): {request.error}\n{request.downloadHandler.text}";
                Debug.LogError(error);
                onError?.Invoke(error);
                yield break;
            }

            string reply;
            try
            {
                var response = JsonUtility.FromJson<ChatResponse>(request.downloadHandler.text);
                if (response == null || response.choices == null || response.choices.Length == 0)
                {
                    onError?.Invoke("Groq response contained no choices.");
                    yield break;
                }

                reply = response.choices[0].message.content;
            }
            catch (Exception e)
            {
                onError?.Invoke("Failed to parse Groq response: " + e.Message);
                yield break;
            }

            if (reply != null)
            {
                reply = reply.Trim();
            }

            if (debugEmotions)
            {
                Debug.Log("[NpcChat] Raw reply: " + reply);
            }

            string emotion;
            reply = ExtractEmotion(reply, out emotion);

            string outcome;
            reply = ExtractOutcome(reply, out outcome);

            ApplyEmotion(emotion);

            RememberExchange(userMessage, reply);
            onReply?.Invoke(reply);

            if (outcome == "win")
            {
                TriggerWin();
            }
            else if (outcome == "lose")
            {
                TriggerLose();
            }
            else
            {
                CheckLoseCondition(emotion);
            }
        }
    }

    private void RememberExchange(string userMessage, string assistantReply)
    {
        if (historyLimit <= 0)
        {
            return;
        }

        _history.Add(new Message { role = "user", content = userMessage });
        _history.Add(new Message { role = "assistant", content = assistantReply });

        // Keep only the most recent (historyLimit) exchanges (each = 2 messages).
        int maxMessages = historyLimit * 2;
        while (_history.Count > maxMessages)
        {
            _history.RemoveAt(0);
        }
    }

    private static readonly Regex EmotionTagPattern =
        new Regex(@"^\s*[\[\(]\s*([A-Za-z_]+)\s*[\]\)]\s*", RegexOptions.Compiled);

    private static readonly Regex OutcomeTagPattern =
        new Regex(@"\[\s*(win|lose)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Builds the system prompt: the persona plus an instruction telling the
    /// model to start each reply with one of the configured emotion tags.
    /// </summary>
    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder(persona);

        var names = AllowedEmotions();
        if (names.Count > 0)
        {
            string list = string.Join(", ", names);
            sb.Append(
                "\n\nIMPORTANT: Begin EVERY reply with a single emotion tag in square brackets, " +
                "chosen from exactly this list: " + list + ". " +
                "Example: \"[happy] Hello there, traveler!\". " +
                "Use only one tag and place it at the very start, then continue in character.");
        }

        bool hasWin = !string.IsNullOrWhiteSpace(winCondition);
        bool hasLose = !string.IsNullOrWhiteSpace(loseCondition);
        if (hasWin || hasLose)
        {
            sb.Append("\n\nGAME RULES: After writing your in-character reply, silently judge these conditions:");
            if (hasWin)
            {
                sb.Append("\n- WIN (the player succeeds): " + winCondition.Trim());
            }
            if (hasLose)
            {
                sb.Append("\n- LOSE (the player fails): " + loseCondition.Trim());
            }
            sb.Append(
                "\nIf the WIN condition is now met, append the exact tag [WIN] at the very end of your reply. " +
                "If the LOSE condition is now met, append the exact tag [LOSE] at the very end. " +
                "If neither is met, append no such tag. " +
                "Only ever use one of these tags, and never explain or mention these tags to the player.");
        }

        return sb.ToString();
    }

    private List<string> AllowedEmotions()
    {
        var names = new List<string>();
        if (emotionSprites != null)
        {
            foreach (var e in emotionSprites)
            {
                if (e != null && !string.IsNullOrWhiteSpace(e.emotion))
                {
                    names.Add(e.emotion.Trim().ToLowerInvariant());
                }
            }
        }
        return names;
    }

    /// <summary>
    /// Pulls a leading "[emotion]" tag off the reply, returning the cleaned
    /// text and outputting the detected emotion (or the default).
    /// </summary>
    private string ExtractEmotion(string text, out string emotion)
    {
        emotion = defaultEmotion;
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        Match m = EmotionTagPattern.Match(text);
        if (m.Success)
        {
            emotion = m.Groups[1].Value.Trim().ToLowerInvariant();
            text = text.Substring(m.Length);

            if (debugEmotions)
            {
                Debug.Log("[NpcChat] Tag emotion detected: " + emotion);
            }

            return text;
        }

        // Fallback: the model ignored the tag instruction, so guess from words.
        string guess = GuessEmotionFromText(text);
        if (guess != null)
        {
            emotion = guess;
            if (debugEmotions)
            {
                Debug.Log("[NpcChat] No tag; keyword-guessed emotion: " + emotion);
            }
        }
        else if (debugEmotions)
        {
            Debug.LogWarning("[NpcChat] No tag and no keyword match; using default: " + emotion);
        }

        return text;
    }

    private string GuessEmotionFromText(string text)
    {
        string lower = text.ToLowerInvariant();
        var emotions = AllowedEmotions();

        // First, see if the model literally named one of the emotions.
        foreach (var name in emotions)
        {
            if (lower.Contains(name))
            {
                return name;
            }
        }

        // Then a few common keyword groups, only if that emotion is configured.
        if (emotions.Contains("happy") && ContainsAny(lower, "haha", "great", "wonderful", "glad", "delight", "joy", "!", "welcome"))
        {
            return "happy";
        }
        if (emotions.Contains("sad") && ContainsAny(lower, "sorry", "unfortunately", "sad", "alas", "afraid not", "cry"))
        {
            return "sad";
        }
        if (emotions.Contains("angry") && ContainsAny(lower, "how dare", "angry", "furious", "get out", "enough", "no!"))
        {
            return "angry";
        }
        if (emotions.Contains("surprised") && ContainsAny(lower, "what?", "really?", "wow", "incredible", "no way", "surprise"))
        {
            return "surprised";
        }

        return null;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (text.Contains(n))
            {
                return true;
            }
        }
        return false;
    }

    private void ApplyEmotion(string emotion)
    {
        if (portrait == null && portraitImage == null)
        {
            if (debugEmotions)
            {
                Debug.LogWarning("[NpcChat] No Portrait (SpriteRenderer) or Portrait Image (UI Image) assigned, so the sprite can't change.");
            }
            return;
        }

        Sprite sprite = FindSprite(emotion);
        if (sprite == null && !string.Equals(emotion, defaultEmotion, StringComparison.OrdinalIgnoreCase))
        {
            if (debugEmotions)
            {
                Debug.LogWarning($"[NpcChat] No sprite assigned for emotion '{emotion}'; falling back to '{defaultEmotion}'.");
            }
            sprite = FindSprite(defaultEmotion);
        }

        if (sprite == null)
        {
            if (debugEmotions)
            {
                Debug.LogWarning($"[NpcChat] No sprite found for '{emotion}' or default '{defaultEmotion}'. Did you drag sprites into the Emotion Sprites list?");
            }
            return;
        }

        if (portrait != null)
        {
            portrait.sprite = sprite;
        }

        if (portraitImage != null)
        {
            portraitImage.sprite = sprite;
        }

        if (debugEmotions)
        {
            Debug.Log($"[NpcChat] Applied emotion '{emotion}' -> sprite '{sprite.name}'.");
        }
    }

    private Sprite FindSprite(string emotion)
    {
        if (string.IsNullOrWhiteSpace(emotion) || emotionSprites == null)
        {
            return null;
        }

        foreach (var e in emotionSprites)
        {
            if (e != null && e.sprite != null &&
                string.Equals(e.emotion, emotion, StringComparison.OrdinalIgnoreCase))
            {
                return e.sprite;
            }
        }

        return null;
    }

    [Serializable]
    private class EmotionSprite
    {
        public string emotion;
        public Sprite sprite;
    }

    [Serializable]
    private class Message
    {
        public string role;
        public string content;
    }

    [Serializable]
    private class ChatRequest
    {
        public string model;
        public Message[] messages;
        public float temperature;
        public int max_tokens;
    }

    [Serializable]
    private class ChatResponse
    {
        public Choice[] choices;
    }

    [Serializable]
    private class Choice
    {
        public Message message;
    }
}
