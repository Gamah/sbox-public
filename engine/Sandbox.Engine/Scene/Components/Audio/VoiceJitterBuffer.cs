using System.Buffers;

namespace Sandbox;

/// <summary>
/// A small managed jitter buffer that sits between voice decode and the playback
/// <see cref="SoundStream"/>. It absorbs network arrival-time variance by holding
/// decoded PCM until a target cushion has accumulated (the "gate"), then releasing
/// it to the stream. After an underrun the gate re-arms so the cushion is rebuilt.
///
/// This is opt-in: with a target of zero samples the gate is always open and every
/// frame passes straight through, matching the un-buffered behaviour. Everything
/// runs on the main thread (decode callback and per-frame pump), so there is no
/// locking.
/// </summary>
internal sealed class VoiceJitterBuffer
{
	private readonly Queue<PooledFrame> frames = new();

	/// <summary>
	/// Total number of samples currently held in the managed FIFO (not yet written
	/// to the stream).
	/// </summary>
	public int QueuedSamples { get; private set; }

	/// <summary>
	/// True once the cushion has filled and we're releasing to the stream. Resets to
	/// false after an underrun so the cushion is rebuilt.
	/// </summary>
	private bool open;

	private readonly struct PooledFrame
	{
		public readonly short[] Buffer;
		public readonly int Length;

		public PooledFrame( short[] buffer, int length )
		{
			Buffer = buffer;
			Length = length;
		}
	}

	/// <summary>
	/// Copy a decoded frame into the buffer. The incoming span is transient (a reused
	/// scratch buffer) so it must be copied, never retained.
	/// </summary>
	public void Enqueue( ReadOnlySpan<short> samples )
	{
		if ( samples.Length <= 0 )
			return;

		var pooled = ArrayPool<short>.Shared.Rent( samples.Length );
		samples.CopyTo( pooled );
		frames.Enqueue( new PooledFrame( pooled, samples.Length ) );
		QueuedSamples += samples.Length;
	}

	/// <summary>
	/// Release buffered audio to the stream, honouring the gate. Call after enqueuing
	/// and once per frame so the gate can open / drain even on ticks with no new data.
	/// </summary>
	public void Pump( SoundStream stream, int targetSamples )
	{
		if ( stream is null )
			return;

		// Fill the cushion before we start releasing.
		if ( !open )
		{
			if ( QueuedSamples < targetSamples )
				return;

			open = true;
		}

		// The native buffer has drained and we've nothing left to hand it: the stream
		// underran. Re-arm the gate so the next talk-spurt rebuilds the cushion.
		if ( QueuedSamples == 0 && stream.QueuedSampleCount == 0 )
		{
			open = false;
			return;
		}

		while ( frames.Count > 0 )
		{
			// Don't overrun the native buffer's writable headroom.
			var headroom = stream.MaxWriteSampleCount;
			if ( headroom <= 0 )
				break;

			var frame = frames.Peek();
			if ( frame.Length > headroom )
				break;

			frames.Dequeue();
			stream.WriteData( frame.Buffer.AsSpan( 0, frame.Length ) );
			QueuedSamples -= frame.Length;
			ArrayPool<short>.Shared.Return( frame.Buffer );
		}
	}

	/// <summary>
	/// Drop everything and return pooled arrays. Called when the stream is recreated or
	/// the component is disabled.
	/// </summary>
	public void Reset()
	{
		while ( frames.Count > 0 )
		{
			ArrayPool<short>.Shared.Return( frames.Dequeue().Buffer );
		}

		QueuedSamples = 0;
		open = false;
	}
}
