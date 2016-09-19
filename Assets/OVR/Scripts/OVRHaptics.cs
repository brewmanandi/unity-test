﻿using UnityEngine;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class OVRHaptics
{
	public readonly static OVRHapticsChannel[] Channels;
	public readonly static OVRHapticsChannel LeftChannel;
	public readonly static OVRHapticsChannel RightChannel;

	private readonly static OVRHapticsOutput[] m_outputs;

	static OVRHaptics()
	{
		Config.Load();

		m_outputs = new OVRHapticsOutput[]
		{
			new OVRHapticsOutput((uint)OVRPlugin.Controller.LTouch),
			new OVRHapticsOutput((uint)OVRPlugin.Controller.RTouch),
		};

		Channels = new OVRHapticsChannel[]
		{
			LeftChannel = new OVRHapticsChannel(0),
			RightChannel = new OVRHapticsChannel(1),
		};
	}

	public static class Config
	{
		public static int SampleRateHz { get; private set; }
		public static int SampleSizeInBytes { get; private set; }
		public static int MinimumSafeSamplesQueued { get; private set; }
		public static int MinimumBufferSamplesCount { get; private set; }
		public static int OptimalBufferSamplesCount { get; private set; }
		public static int MaximumBufferSamplesCount { get; private set; }

		static Config()
		{
			Load();
		}

		public static void Load()
		{
			OVRPlugin.HapticsDesc desc = OVRPlugin.GetControllerHapticsDesc((uint)OVRPlugin.Controller.RTouch);

			SampleRateHz = desc.SampleRateHz;
			SampleSizeInBytes = desc.SampleSizeInBytes;
			MinimumSafeSamplesQueued = desc.MinimumSafeSamplesQueued;
			MinimumBufferSamplesCount = desc.MinimumBufferSamplesCount;
			OptimalBufferSamplesCount = desc.OptimalBufferSamplesCount;
			MaximumBufferSamplesCount = desc.MaximumBufferSamplesCount;
		}
	}

	public class OVRHapticsChannel
	{
		private OVRHapticsOutput m_output;

		public OVRHapticsChannel(uint outputIndex)
		{
			m_output = m_outputs[outputIndex];
		}

		public void Preempt(OVRHapticsClip clip)
		{
			m_output.Preempt(clip);
		}

		public void Queue(OVRHapticsClip clip)
		{
			m_output.Queue(clip);
		}

		public void Mix(OVRHapticsClip clip)
		{
			m_output.Mix(clip);
		}

		public void Clear()
		{
			m_output.Clear();
		}
	}

	private class OVRHapticsOutput
	{
		private class NativeBuffer
		{
			private int m_numBytes = 0;
			private IntPtr m_ptr = IntPtr.Zero;

			public IntPtr GetPointer(int byteOffset = 0)
			{
				if (byteOffset < 0 || byteOffset >= m_numBytes)
				{
					//Debug.LogError("Attempted invalid access - Allocated: " + m_numBytes + " Requested: " + byteOffset);
					return IntPtr.Zero;
				}

				return (byteOffset == 0) ? m_ptr : new IntPtr(m_ptr.ToInt64() + byteOffset);
			}

			public NativeBuffer(int numBytes)
			{
				m_ptr = Marshal.AllocHGlobal(numBytes);
				m_numBytes = numBytes;
			}

			~NativeBuffer()
			{
				if (m_ptr != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(m_ptr);
					m_ptr = IntPtr.Zero;
					m_numBytes = 0;
				}
			}
		}

		private class ClipPlaybackTracker
		{
			public int ReadCount { get; set; }
			public OVRHapticsClip Clip { get; set; }

			public ClipPlaybackTracker(OVRHapticsClip clip)
			{
				Clip = clip;
			}
		}

		private bool m_lowLatencyMode = true;
		private int m_prevSamplesQueued = 0;
		private float m_prevSamplesQueuedTime = 0;
		private int m_numPredictionHits = 0;
		private int m_numPredictionMisses = 0;
		private int m_numUnderruns = 0;
		private List<ClipPlaybackTracker> m_pendingClips = new List<ClipPlaybackTracker>();
		private uint m_controller = 0;
		private NativeBuffer m_nativeBuffer = new NativeBuffer(OVRHaptics.Config.MaximumBufferSamplesCount * OVRHaptics.Config.SampleSizeInBytes);
		private OVRHapticsClip m_paddingClip = new OVRHapticsClip();

		public OVRHapticsOutput(uint controller)
		{
			m_controller = controller;
		}

		public void Process()
		{
			var hapticsState = OVRPlugin.GetControllerHapticsState(m_controller);

			float elapsedTime = Time.realtimeSinceStartup - m_prevSamplesQueuedTime;
			if (m_prevSamplesQueued > 0)
			{
				int expectedSamples = m_prevSamplesQueued - (int)(elapsedTime * OVRHaptics.Config.SampleRateHz + 0.5f);
				if (expectedSamples < 0)
					expectedSamples = 0;

				if ((hapticsState.SamplesQueued - expectedSamples) == 0)
					m_numPredictionHits++;
				else
					m_numPredictionMisses++;

				//Debug.Log(hapticsState.SamplesAvailable + "a " + hapticsState.SamplesQueued + "q " + expectedSamples + "e "
				//+ "Prediction Accuracy: " + m_numPredictionHits / (float)(m_numPredictionMisses + m_numPredictionHits));

				if ((expectedSamples > 0) && (hapticsState.SamplesQueued == 0))
				{
					m_numUnderruns++;
					//Debug.LogError("Samples Underrun (" + m_controller + " #" + m_numUnderruns + ") -"
					//        + " Expected: " + expectedSamples
					//        + " Actual: " + hapticsState.SamplesQueued);
				}

				m_prevSamplesQueued = hapticsState.SamplesQueued;
				m_prevSamplesQueuedTime = Time.realtimeSinceStartup;
			}

			int desiredSamplesCount = OVRHaptics.Config.OptimalBufferSamplesCount;
			if (m_lowLatencyMode)
			{
				float sampleRateMs = 1000.0f / (float)OVRHaptics.Config.SampleRateHz;
				float elapsedMs = elapsedTime * 1000.0f;
				int samplesNeededPerFrame = (int)Mathf.Ceil(elapsedMs / sampleRateMs);
				int lowLatencySamplesCount = OVRHaptics.Config.MinimumSafeSamplesQueued + samplesNeededPerFrame;

				if (lowLatencySamplesCount < desiredSamplesCount)
					desiredSamplesCount = lowLatencySamplesCount;
			}

			if (hapticsState.SamplesQueued > desiredSamplesCount)
				return;

			if (desiredSamplesCount > OVRHaptics.Config.MaximumBufferSamplesCount)
				desiredSamplesCount = OVRHaptics.Config.MaximumBufferSamplesCount;
			if (desiredSamplesCount > hapticsState.SamplesAvailable)
				desiredSamplesCount = hapticsState.SamplesAvailable;

			int acquiredSamplesCount = 0;
			int clipIndex = 0;
			while(acquiredSamplesCount < desiredSamplesCount && clipIndex < m_pendingClips.Count)
			{
				int numSamplesToCopy = desiredSamplesCount - acquiredSamplesCount;
				int remainingSamplesInClip = m_pendingClips[clipIndex].Clip.Count - m_pendingClips[clipIndex].ReadCount;
				if (numSamplesToCopy > remainingSamplesInClip)
					numSamplesToCopy = remainingSamplesInClip;

				if (numSamplesToCopy > 0)
				{
					int numBytes = numSamplesToCopy * OVRHaptics.Config.SampleSizeInBytes;
					int dstOffset = acquiredSamplesCount * OVRHaptics.Config.SampleSizeInBytes;
					int srcOffset = m_pendingClips[clipIndex].ReadCount * OVRHaptics.Config.SampleSizeInBytes;
					Marshal.Copy(m_pendingClips[clipIndex].Clip.Samples, srcOffset, m_nativeBuffer.GetPointer(dstOffset), numBytes);

					m_pendingClips[clipIndex].ReadCount += numSamplesToCopy;
					acquiredSamplesCount += numSamplesToCopy;
				}

				clipIndex++;
			}

			for (int i = m_pendingClips.Count - 1; i >= 0 && m_pendingClips.Count > 0; i--)
			{
				if (m_pendingClips[i].ReadCount >= m_pendingClips[i].Clip.Count)
					m_pendingClips.RemoveAt(i);
			}

			int desiredPadding = desiredSamplesCount - (hapticsState.SamplesQueued + acquiredSamplesCount);
			if (desiredPadding < (OVRHaptics.Config.MinimumBufferSamplesCount - acquiredSamplesCount))
				desiredPadding = (OVRHaptics.Config.MinimumBufferSamplesCount - acquiredSamplesCount);
			if (desiredPadding > hapticsState.SamplesAvailable)
				desiredPadding = hapticsState.SamplesAvailable;

			if (desiredPadding > 0)
			{
				int numBytes = desiredPadding * OVRHaptics.Config.SampleSizeInBytes;
				int dstOffset = acquiredSamplesCount * OVRHaptics.Config.SampleSizeInBytes;
				int srcOffset = 0;
				Marshal.Copy(m_paddingClip.Samples, srcOffset, m_nativeBuffer.GetPointer(dstOffset), numBytes);

				acquiredSamplesCount += desiredPadding;
			}

			if (acquiredSamplesCount > 0)
			{
				OVRPlugin.HapticsBuffer hapticsBuffer;
				hapticsBuffer.Samples = m_nativeBuffer.GetPointer();
				hapticsBuffer.SamplesCount = acquiredSamplesCount;
	
				OVRPlugin.SetControllerHaptics(m_controller, hapticsBuffer);

				hapticsState = OVRPlugin.GetControllerHapticsState(m_controller);
				m_prevSamplesQueued = hapticsState.SamplesQueued;
				m_prevSamplesQueuedTime = Time.realtimeSinceStartup;
			}
		}

		public void Preempt(OVRHapticsClip clip)
		{
			m_pendingClips.Clear();
			m_pendingClips.Add(new ClipPlaybackTracker(clip));
		}

		public void Queue(OVRHapticsClip clip)
		{
			m_pendingClips.Add(new ClipPlaybackTracker(clip));
		}

		public void Mix(OVRHapticsClip clip)
		{
			int numClipsToMix = 0;
			int numSamplesToMix = 0;
			int numSamplesRemaining = clip.Count;

			while (numSamplesRemaining > 0 && numClipsToMix < m_pendingClips.Count)
			{
				int numSamplesRemainingInClip = m_pendingClips[numClipsToMix].Clip.Count - m_pendingClips[numClipsToMix].ReadCount;
				numSamplesRemaining -= numSamplesRemainingInClip;
				numSamplesToMix += numSamplesRemainingInClip;
				numClipsToMix++;
			}

			if (numSamplesRemaining > 0)
			{
				numSamplesToMix += numSamplesRemaining;
				numSamplesRemaining = 0;
			}

			if (numClipsToMix > 0)
			{
				OVRHapticsClip mixClip = new OVRHapticsClip(numSamplesToMix);

				OVRHapticsClip a = clip;
				int aReadCount = 0;

				for (int i = 0; i < numClipsToMix; i++)
				{
					OVRHapticsClip b = m_pendingClips[i].Clip;
					for(int bReadCount = m_pendingClips[i].ReadCount; bReadCount < b.Count; bReadCount++)
					{
						if (OVRHaptics.Config.SampleSizeInBytes == 1)
						{
							byte sample = 0; // TODO support multi-byte samples
							if ((aReadCount < a.Count) && (bReadCount < b.Count))
							{
								sample = (byte)(Mathf.Clamp(a.Samples[aReadCount] + b.Samples[bReadCount], 0, System.Byte.MaxValue)); // TODO support multi-byte samples
								aReadCount++;
							}
							else if (bReadCount < b.Count)
							{
								sample = b.Samples[bReadCount]; // TODO support multi-byte samples
							}
	
							mixClip.WriteSample(sample); // TODO support multi-byte samples
						}
					}
				}

				while (aReadCount < a.Count)
				{
					if (OVRHaptics.Config.SampleSizeInBytes == 1)
					{
						mixClip.WriteSample(a.Samples[aReadCount]); // TODO support multi-byte samples
					}
					aReadCount++;
				}

				m_pendingClips[0] = new ClipPlaybackTracker(mixClip);
				for (int i = 1; i < numClipsToMix; i++)
				{
					m_pendingClips.RemoveAt(1);
				}
			}
			else
			{
				m_pendingClips.Add(new ClipPlaybackTracker(clip));
			}
		}

		public void Clear()
		{
			m_pendingClips.Clear();
		}
	}

	public static void Process()
	{
		Config.Load();

		for (int i = 0; i < m_outputs.Length; i++)
		{
			m_outputs[i].Process();
		}
	}
}

