using System;
using System.Collections.Generic;
using Silk.NET.Input;

namespace silkgltriangle;

/// <summary>
/// An <see cref="IKeyboard"/> whose pressed keys are set by code instead of the OS, so visual
/// scenarios drive the real <c>Camera.Update</c> path without faking window input.
/// </summary>
public sealed class ScriptedKeyboard : IKeyboard
{
    private readonly HashSet<Key> _pressed = new();

    public void Press(IEnumerable<Key> keys)
    {
        _pressed.Clear();
        foreach (var key in keys)
            _pressed.Add(key);
    }

    public void ReleaseAll() => _pressed.Clear();

    public bool IsKeyPressed(Key key) => _pressed.Contains(key);
    public bool IsKeyReleased(Key key) => !_pressed.Contains(key);
    public bool IsScancodePressed(int scancode) => false;

    public string ClipboardText { get => ""; set { } }
    public int Index => 0;
    public string Name => nameof(ScriptedKeyboard);
    public bool IsConnected => true;
    public IReadOnlyList<Key> SupportedKeys => (IReadOnlyList<Key>)Enum.GetValues<Key>();

    public event Action<IKeyboard, Key, int>? KeyDown { add { } remove { } }
    public event Action<IKeyboard, Key, int>? KeyUp { add { } remove { } }
    public event Action<IKeyboard, char>? KeyChar { add { } remove { } }

    public void BeginInput() { }
    public void EndInput() { }
    public void Dispose() { }
}
