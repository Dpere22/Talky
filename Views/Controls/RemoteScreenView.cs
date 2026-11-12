using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Talky.Interop;
using Talky.Services.ScreenShare;

namespace Talky.Views.Controls;

/// <summary>
/// Shows the peer's shared screen. Decoded frames arrive on a network thread; UI updates are
/// coalesced so at most one copy into the <see cref="WriteableBitmap"/> is pending at a time.
/// </summary>
public sealed partial class RemoteScreenView : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(IRemoteScreen), typeof(RemoteScreenView), new PropertyMetadata(null, OnSourceChanged));

    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly DispatcherQueue _dispatcher;
    private WriteableBitmap? _bitmap;
    private int _renderPending;
    private bool _loaded;

    public RemoteScreenView()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Content = _image;
        IsTabStop = false;
        Loaded += (_, _) =>
        {
            _loaded = true;
            Attach(Source);
            Render();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            Detach(Source);
        };
    }

    public IRemoteScreen? Source
    {
        get => (IRemoteScreen?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (RemoteScreenView)d;
        if (!view._loaded)
        {
            return;
        }

        view.Detach(e.OldValue as IRemoteScreen);
        view.Attach(e.NewValue as IRemoteScreen);
        view.Render();
    }

    private void Attach(IRemoteScreen? source)
    {
        if (source is not null)
        {
            source.FrameReady += OnFrameReady;
            source.StateChanged += OnFrameReady;
        }
    }

    private void Detach(IRemoteScreen? source)
    {
        if (source is not null)
        {
            source.FrameReady -= OnFrameReady;
            source.StateChanged -= OnFrameReady;
        }
    }

    private void OnFrameReady(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _renderPending, 1) == 0)
        {
            _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
            {
                Volatile.Write(ref _renderPending, 0);
                Render();
            });
        }
    }

    private unsafe void Render()
    {
        var source = Source;
        if (source is null || !source.IsActive || source.Width <= 0 || source.Height <= 0)
        {
            _image.Source = null;
            _bitmap = null;
            return;
        }

        int width = source.Width, height = source.Height;
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height);
            _image.Source = _bitmap;
        }

        var pixels = BufferInterop.GetPointer(_bitmap.PixelBuffer);
        if (source.CopyFrame(new Span<byte>(pixels, width * height * 4), out var copiedWidth, out var copiedHeight)
            && copiedWidth == width && copiedHeight == height)
        {
            _bitmap.Invalidate();
        }
    }
}
