using System;
using System.Linq;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace silkgltriangle;

public class InputManager : IDisposable
{
    public IInputContext InputContext { get; private set; }
    public IKeyboard? Keyboard { get; private set; }
    public IMouse? Mouse { get; private set; }

    public InputManager(IWindow window)
    {
        InputContext = window.CreateInput();
        Keyboard = InputContext.Keyboards.FirstOrDefault();
        Mouse = InputContext.Mice.FirstOrDefault();
    }

    public void Dispose()
    {
        InputContext?.Dispose();
    }
}