using System.Collections.Generic;
using System.Text;

namespace Sandbox.Rendering;

/// <summary>
/// Per-frame log of what every view asked of the directional (CSM) path and what it got back, for
/// finding out why sun shadows drop out on some frames while VR is active.
///
/// <para>Logs a frame only when its shape CHANGES — the views present, and for each of them the
/// cascade count, whether it reused another view's fit, and the memory it allocated. At 120Hz
/// across three views a per-frame log is unreadable, and the thing being hunted is the transition,
/// so the transitions are the whole signal.</para>
///
/// <para>Off unless <c>r.shadows.csm.log</c> is set, so it can be turned on and off inside one
/// session with VR toggled between runs.</para>
/// </summary>
internal partial class ShadowMapper
{
	[ConVar( "r.shadows.csm.log", Help = "Log the directional shadow path's per-frame shape, and only when it changes: which views asked, how many cascades each got, and which reused another view's fit." )]
	public static bool CSMLog { get; set; }

	/// <summary>Set in <see cref="FindOrCreateDirectionalShadowMaps"/>: whether this view took
	/// another view's published cascade fit instead of computing its own.</summary>
	bool _logBorrowed;

	/// <summary>Each cascade-cache condition as the view found it, so a view that fit its own
	/// cascades says which condition sent it there.</summary>
	string _logGate;

	/// <summary>A short stable name per mapper instance. One mapper exists per light binner, so
	/// this is what shows that more than one is in play at all.</summary>
	int _logId;
	static int _logIds;

	// The frame being collected, the calls collected for it, and the last shape actually printed.
	static ulong _logFrame;
	static readonly List<string> _logCalls = new();
	static string _logLast = "";
	static int _logRepeats;

	/// <summary>Record one directional call. Called after the work, so the values are what the
	/// view ended up with rather than what it asked for.</summary>
	void LogDirectional()
	{
		if ( !CSMLog ) return;

		if ( _logId == 0 ) _logId = ++_logIds;

		ulong frame = Application.FrameCount;
		if ( frame != _logFrame )
		{
			FlushDirectionalLog();
			_logFrame = frame;
			_logCalls.Clear();
		}

		var size = View.ViewportSize;
		_logCalls.Add( $"m{_logId} {size.x:0}x{size.y:0} cascades={CascadeShadowMapCount}"
			+ $"{( _logBorrowed ? " REUSED" : $" OWN[{_logGate}]" )}"
			+ $"{( DirectionalShadowMemorySize == 0 ? " mem=0" : "" )}" );
	}

	/// <summary>Print the collected frame if its shape differs from the last one printed, and
	/// otherwise just count it. The count is what says "this held still for 240 frames and then
	/// changed", which is the shape of a dropout.</summary>
	static void FlushDirectionalLog()
	{
		if ( _logCalls.Count == 0 ) return;

		var shape = new StringBuilder();
		for ( int i = 0; i < _logCalls.Count; i++ )
		{
			if ( i > 0 ) shape.Append( "  |  " );
			shape.Append( _logCalls[i] );
		}

		var text = shape.ToString();
		if ( text == _logLast )
		{
			_logRepeats++;
			return;
		}

		if ( _logRepeats > 0 )
			Log.Info( $"[csm] ... same for {_logRepeats} frames" );

		Log.Info( $"[csm] f{_logFrame}  {text}" );
		_logLast = text;
		_logRepeats = 0;
	}
}
