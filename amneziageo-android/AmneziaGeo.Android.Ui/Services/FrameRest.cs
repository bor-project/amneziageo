using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Android.Views;
using Avalonia;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Stops the frames of the window while nothing on it changes, and brings them back with the next change.
/// </summary>
internal sealed class FrameRest : Java.Lang.Object, Choreographer.IFrameCallback
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string Tag = "AmneziaGeoFrames";

    private static FrameRest? _attached;

    private readonly object _gate = new();
    private readonly Choreographer.IFrameCallback _frames;
    private readonly Choreographer _choreographer;
    private long _busyAt = System.Environment.TickCount64;
    private bool _resting = true;
    private bool _asked;
    private volatile bool _shown;

    /// <summary>
    /// ctor
    /// </summary>
    private FrameRest(Choreographer.IFrameCallback frames, Choreographer choreographer)
    {
        _frames = frames;
        _choreographer = choreographer;
    }

    /// <summary>
    /// Starts following what the window sends to be drawn, once in a process.
    /// </summary>
    [DynamicDependency("add_AfterCommit", typeof(Compositor))]
    public static void Attach()
    {
        if (_attached is not null)
        {
            return;
        }

        var locator = typeof(AvaloniaLocator).GetMethod("get_Current", Any)?.Invoke(null, null) as AvaloniaLocator;
        var service = typeof(AvaloniaLocator).GetMethod("GetService", Any, [typeof(Type)]);
        var known = Type.GetType("Avalonia.Android.ChoreographerTimer, Avalonia.Android");
        var listen = typeof(Compositor).GetMethod("add_AfterCommit", Any);
        var timer = locator is null ? null : service?.Invoke(locator, [typeof(IRenderTimer)]);
        var compositor = locator is null ? null : service?.Invoke(locator, [typeof(Compositor)]);
        if (timer is not Choreographer.IFrameCallback frames
            || compositor is not Compositor
            || listen is null
            || known is null
            || !known.IsInstanceOfType(timer)
            || known.GetField("_choreographer", Any)?.GetValue(timer) is not TaskCompletionSource<Choreographer> source
            || !source.Task.IsCompletedSuccessfully)
        {
            global::Android.Util.Log.Info(Tag, "the frame timer is not the one this build knows, the frames stay as they are");
            return;
        }

        var rest = new FrameRest(frames, source.Task.Result) { _shown = MainActivity.Shown };
        listen.Invoke(compositor, [new Action(rest.Busy)]);
        MainActivity.ShownChanged += rest.Shown;
        _attached = rest;
        global::Android.Util.Log.Info(Tag, "the window rests its frames while nothing on it changes");
    }

    /// <summary>
    /// Brings the frames back ahead of what the user does to the window.
    /// </summary>
    public static void Wake()
    {
        _attached?.Busy();
    }

    /// <inheritdoc/>
    public void DoFrame(long frameTimeNanos)
    {
        var beat = false;
        lock (_gate)
        {
            _choreographer.RemoveFrameCallback(this);
            if (!_resting)
            {
                if (_shown && !FramePace.Rests(System.Environment.TickCount64, _busyAt))
                {
                    _choreographer.PostFrameCallback(this);
                    return;
                }

                _resting = true;
                _choreographer.RemoveFrameCallback(_frames);
                if (_shown)
                {
                    _choreographer.PostFrameCallbackDelayed(this, FramePace.BeatMs);
                }

                return;
            }

            if (_asked)
            {
                _asked = false;
                _resting = false;
            }
            else if (_shown)
            {
                beat = true;
                _choreographer.PostFrameCallbackDelayed(this, FramePace.BeatMs);
            }
            else
            {
                return;
            }

            _choreographer.RemoveFrameCallback(_frames);
        }

        _frames.DoFrame(frameTimeNanos);
        lock (_gate)
        {
            if (!beat)
            {
                _choreographer.PostFrameCallback(this);
            }
            else if (_resting)
            {
                _choreographer.RemoveFrameCallback(_frames);
            }
        }
    }

    // Notes a change of the window and asks for the frames back where they are stopped.
    private void Busy()
    {
        lock (_gate)
        {
            _busyAt = System.Environment.TickCount64;
            if (!_resting || _asked || !_shown)
            {
                return;
            }

            _asked = true;
            _choreographer.RemoveFrameCallback(this);
            _choreographer.PostFrameCallback(this);
        }
    }

    // Follows the window onto the screen and off it.
    private void Shown(bool shown)
    {
        _shown = shown;
        if (shown)
        {
            Busy();
        }
    }
}
