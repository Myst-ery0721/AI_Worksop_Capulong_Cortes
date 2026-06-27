# NPC Chat (Unity + Groq)

A Unity NPC dialogue system that talks to the [Groq](https://groq.com) chat
completions API (OpenAI-compatible) using `UnityWebRequest`. The NPC remembers
recent conversation turns and changes its sprite based on the emotion of its
reply.

## Features

- Calls `https://api.groq.com/openai/v1/chat/completions` with a bearer token.
- Configurable persona (system prompt), model, temperature, and max tokens.
- Short-term conversation memory (recent turns are resent so the NPC remembers).
- Emotion-driven sprite swapping: the model tags each reply (e.g. `[happy]`) and
  the matching sprite is shown. Includes a keyword fallback if the model omits
  the tag.
- Built-in `OnGUI` chat box (text field + Send button + scrolling log) that needs
  no scene setup, plus optional TextMeshPro `InputField` / `Text` hooks.
- Show/hide the chat box with a hotkey (default **Tab**) or from code.
- Works with the new Input System or the legacy Input Manager.

## Requirements

- Unity 6 (URP 2D project; uses `com.unity.ugui` / TextMeshPro).
- A free Groq API key from <https://console.groq.com/keys>.

## Setup

1. Open the project in Unity. Import **TMP Essentials** if prompted
   (Window → TextMeshPro → Import TMP Essential Resources).
2. Add the `NpcChat` component (`Assets/Scripts/NpcChat.cs`) to a GameObject.
3. In the Inspector, fill in:
   - **API Key** — your Groq key (see security note below).
   - **Model** — e.g. `llama-3.3-70b-versatile`.
   - **Persona** — describe who the NPC is and how it should respond.
4. Press **Play**. With *Show Chat Box* enabled, an on-screen chat box appears in
   the top-left. Type a message and press **Send** (or Enter).

### Emotion sprites

1. Put your sprites under `Assets/Sprites/` (this project includes
   `neutral`, `happy`, `sad`, `angry`, `surprised`). For a sprite sheet, set
   **Sprite Mode: Multiple** and slice it in the Sprite Editor.
2. Assign a **Portrait** (`SpriteRenderer` in the scene) and/or a
   **Portrait Image** (UI `Image` on a Canvas).
3. In the **Emotion Sprites** list, drag a sprite into each emotion row. The
   model is only told the emotion names you have configured.
4. The NPC starts on **Default Emotion** (`neutral`) and switches as it replies.
   Enable **Debug Emotions** to log detection details to the Console.

Sprites used in this project are by **judas la carotte**.

## Controls

| Action            | Default |
| ----------------- | ------- |
| Send message      | Enter / Send button |
| Toggle chat box   | Tab (`toggleKey`) |

You can also call from code:

```csharp
npc.Ask("Hello there!");      // send and display
npc.ToggleChatBox();          // show/hide the OnGUI box
npc.ResetConversation();      // clear memory
```

## Security note

The API key is stored on the component. Do **not** commit it or ship it in a
public build — anyone could extract it. For production, route requests through
your own backend instead of calling Groq directly from the client. This repo's
`.gitignore` excludes generated folders, and no key is committed, but keep the
`apiKey` field empty in any saved scene.

## Project layout

```
Assets/
  Scripts/NpcChat.cs   # the NPC chat + emotion sprite component
  Sprites/             # emotion sprites
  Scenes/              # sample scene
```
