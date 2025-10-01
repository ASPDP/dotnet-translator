using HotkeyListener.Services.SystemSpecificStuff.Interop;

namespace HotkeyListener.Services.SystemSpecificStuff.Keyboard;

internal sealed class DoublePressHotkeyListener : IDisposable, IHotkeySimulationGuard
{
    private readonly KeyboardHook _keyboardHook;
    private readonly Keys[] _monitoredKeys;
    private readonly TimeSpan _threshold;

    private bool _ctrlDown;
    private DateTime _lastPressTime = DateTime.MinValue;
    private volatile bool _suppressEvents;

    public event EventHandler? HotkeyTriggered;

    public DoublePressHotkeyListener(KeyboardHook keyboardHook, TimeSpan threshold, params Keys[] monitoredKeys)
    {
        _keyboardHook = keyboardHook;
        _threshold = threshold;
        _monitoredKeys = monitoredKeys.Length > 0
            ? monitoredKeys
            : new[] { Keys.LControlKey, Keys.RControlKey };

        _keyboardHook.KeyboardEvent += OnKeyboardEvent;
    }

    public void Start()
    {
        _keyboardHook.Start();
    }

    private void OnKeyboardEvent(object? sender, KeyboardEvent keyboardEvent)
    {
        if (_suppressEvents)
        {
            return;
        }

        bool isControlKey = Array.IndexOf(_monitoredKeys, keyboardEvent.Key) >= 0;
        if (!isControlKey)
        {
            if (keyboardEvent.Type == KeyboardEventType.KeyDown)
            {
                ResetState();
            }

            return;
        }

        if (keyboardEvent.Type == KeyboardEventType.KeyDown)
        {
            if (_ctrlDown)
            {
                return;
            }

            _ctrlDown = true;
            var elapsed = DateTime.UtcNow - _lastPressTime;
            if (elapsed <= _threshold)
            {
                _lastPressTime = DateTime.MinValue;
                HotkeyTriggered?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _lastPressTime = DateTime.UtcNow;
            }
        }
        else if (keyboardEvent.Type == KeyboardEventType.KeyUp)
        {
            _ctrlDown = false;
        }
    }

    private void ResetState()
    {
        _lastPressTime = DateTime.MinValue;
        _ctrlDown = false;
    }

    public IDisposable BeginSimulationScope()
    {
        _suppressEvents = true;
        ResetState();
        return new SimulationScope(this);
    }

    private void EndSimulationScope()
    {
        _suppressEvents = false;
        ResetState();
    }

    public void Dispose()
    {
        _keyboardHook.KeyboardEvent -= OnKeyboardEvent;
        _keyboardHook.Dispose();
    }

    private sealed class SimulationScope : IDisposable
    {
        private DoublePressHotkeyListener? _owner;

        public SimulationScope(DoublePressHotkeyListener owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            _owner?.EndSimulationScope();
            _owner = null;
        }
    }
}

internal interface IHotkeySimulationGuard
{
    IDisposable BeginSimulationScope();
}
