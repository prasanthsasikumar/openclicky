using System.Windows.Threading;
using OpenClicky.Core.Platform;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// Builds every OS implementation once, for the composition root. Nothing here starts listening:
/// hooks and listeners start when the app calls their Start() (keyboard monitor, foreground
/// observer, clipboard listener), so constructing this is cheap and side-effect free.
/// </summary>
public sealed class PlatformServices : IDisposable
{
    public PlatformServices(Dispatcher uiDispatcher)
    {
        MainThread = new DispatcherMainThread(uiDispatcher);
        Clipboard = new Win32Clipboard();
        TextInserter = new FrontAppTextInserter(Clipboard);
        // find_on_page types into the find bar the way live dictation types, once UI Automation says it has the focus.
        DesktopActions = new DesktopActionExecutor(TextInserter, FocusedElements);
        WindowList = new WindowList();
        BrowserTabs = new BrowserTabEnumerator(WindowList);
        screens = new Screens();
        ScreenCapture = new ScreenCapture(screens);
        AudioPlayer = new AudioPlayer();
        MicrophonePermission = new MicrophonePermission(CreateAudioCapture);
    }

    public IMainThread MainThread { get; }
    public IGlobalKeyboardMonitor KeyboardMonitor { get; } = new GlobalKeyboardMonitor();
    public IClipboard Clipboard { get; }
    public IClipboardChangeListener ClipboardChanges { get; } = new ClipboardChangeListener();
    public ITextInserter TextInserter { get; }
    public IFocusedElementReader FocusedElements { get; } = new FocusedElementReader();
    public IFrontmostAppObserver FrontmostApp { get; } = new FrontmostAppObserver();
    public IWindowList WindowList { get; }
    public IBrowserTabEnumerator BrowserTabs { get; }
    private readonly Screens screens;

    public IScreens Screens => screens;
    public IScreenCapture ScreenCapture { get; }
    public IScreenTextRecognizer TextRecognizer { get; } = new ScreenTextRecognizer();
    public IMicrophoneDevices Microphones { get; } = new MicrophoneDevices();
    public IAudioPlayer AudioPlayer { get; }
    public IEarconPlayer Earcons { get; } = new EarconPlayer();
    public IMicrophonePermission MicrophonePermission { get; }
    public IOfflineSpeechRecognizer OfflineSpeech { get; } = new WindowsSpeechRecognizer();
    public ISystemVoice SystemVoice { get; } = new WindowsSystemVoice();
    public ILoginItem LoginItem { get; } = new RunKeyLoginItem();
    public INetworkReachability Network { get; } = new NetworkReachability();
    public IDesktopActionExecutor DesktopActions { get; }
    public IProcessRunner Processes { get; } = new ProcessRunner();
    public ISingleInstance SingleInstance { get; } = new SingleInstance();

    /// <summary>A new microphone capture (each consumer — dictation, Realtime, the mic test — owns its own).</summary>
    public IAudioCapture CreateAudioCapture() => new AudioCapture();

    /// <summary>A new Realtime output player (PCM16 mono 24 kHz).</summary>
    public IStreamingPcmPlayer CreateStreamingPlayer(int sampleRate = 24_000) => new StreamingPcmPlayer(sampleRate);

    public void Dispose()
    {
        KeyboardMonitor.Dispose();
        ClipboardChanges.Dispose();
        FrontmostApp.Dispose();
        screens.Dispose();
        Microphones.Dispose();
        Network.Dispose();
        SingleInstance.Dispose();
    }
}
