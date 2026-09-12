using System.Collections.Generic;

namespace NeuroMicrophone.Models;

/// <summary>
/// Битовые флаги модификаторов для WinAPI RegisterHotKey (значения строго
/// соответствуют MOD_ALT / MOD_CONTROL / MOD_SHIFT из user32.dll).
/// </summary>
public static class HotkeyModifiers
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
}

/// <summary>
/// Один вариант горячей клавиши для заглушки микрофона: отображаемое имя
/// плюс модификаторы и виртуальный код клавиши, напрямую пригодные для
/// WinAPI RegisterHotKey. Набор ограничен несколькими явно указанными
/// комбинациями (а не свободным вводом любой клавиши) — это проще и
/// надёжнее реализовать корректно, чем полноценный UI "нажмите нужную
/// комбинацию", и даёт пользователю выбор на случай конфликта комбинации
/// по умолчанию с другой программой (например, записью экрана).
/// </summary>
public sealed class HotkeyOption
{
    public string DisplayName { get; }
    public uint Modifiers { get; }
    public uint VirtualKey { get; }

    public HotkeyOption(string displayName, uint modifiers, uint virtualKey)
    {
        DisplayName = displayName;
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public override string ToString() => DisplayName;

    public static IReadOnlyList<HotkeyOption> BuiltIn { get; } = new List<HotkeyOption>
    {
        new("Ctrl + Shift + M", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4D),
        new("Ctrl + Alt + M", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4D),
        new("Alt + Shift + M", HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x4D),
        new("Ctrl + Shift + K", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4B),
        new("Ctrl + Alt + K", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4B),
        new("Ctrl + Shift + F9", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x78),
    };
}
