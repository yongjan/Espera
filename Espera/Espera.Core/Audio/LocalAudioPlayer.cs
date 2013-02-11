﻿using NAudio;
using NAudio.Wave;
using Rareform.Validation;
using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace Espera.Core.Audio
{
    /// <summary>
    /// An <see cref="AudioPlayer"/> that plays songs from the local harddrive.
    /// </summary>
    internal sealed class LocalAudioPlayer : AudioPlayer
    {
        private readonly BehaviorSubject<TimeSpan> totalTime;
        private WaveChannel32 inputStream;
        private object playerLock;
        private float volume;
        private IWavePlayer wavePlayer;

        public LocalAudioPlayer(Song song)
        {
            if (song == null)
                Throw.ArgumentNullException(() => song);

            this.Song = song;
            this.Volume = 1.0f;

            this.totalTime = new BehaviorSubject<TimeSpan>(TimeSpan.Zero);
            this.playerLock = new object();
        }

        public override TimeSpan CurrentTime
        {
            get { return this.inputStream == null ? TimeSpan.Zero : this.inputStream.CurrentTime; }
            set { this.inputStream.CurrentTime = value; }
        }

        public override IObservable<TimeSpan> TotalTime
        {
            get { return this.totalTime.AsObservable(); }
        }

        public override float Volume
        {
            get { return this.volume; }
            set
            {
                this.volume = value;

                if (this.inputStream != null)
                {
                    this.inputStream.Volume = value;
                }
            }
        }

        public override void Dispose()
        {
            this.Stop();

            lock (this.playerLock)
            {
                if (wavePlayer != null)
                {
                    this.wavePlayer.Dispose();
                    this.wavePlayer = null;
                }

                if (inputStream != null)
                {
                    try
                    {
                        this.inputStream.Dispose();
                    }

                    // TODO: NAudio sometimes thows an exception here for unknown reasons
                    catch (MmException)
                    { }

                    this.inputStream = null;
                }
            }
        }

        public override void Load()
        {
            lock (this.playerLock)
            {
                this.wavePlayer = new WaveOutEvent();

                try
                {
                    this.CreateInputStream(this.Song);
                    this.wavePlayer.Init(inputStream);
                }

                // NAudio can throw a broad range of exceptions when opening a song, so we catch everything
                catch (Exception ex)
                {
                    throw new SongLoadException("Song could not be loaded.", ex);
                }

                this.totalTime.OnNext(this.inputStream.TotalTime);
            }
        }

        public override void Pause()
        {
            lock (this.playerLock)
            {
                if (this.wavePlayer == null || this.inputStream == null || this.wavePlayer.PlaybackState == NAudio.Wave.PlaybackState.Paused)
                    return;

                this.wavePlayer.Pause();

                this.EnsureState(NAudio.Wave.PlaybackState.Paused);
                this.SetPlaybackState(AudioPlayerState.Paused);
            }
        }

        public override void Play()
        {
            lock (this.playerLock)
            {
                if (this.wavePlayer == null || this.inputStream == null || this.wavePlayer.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                    return;

                // Create a new thread, so that we can spawn the song state check on the same thread as the play method
                // With this, we can avoid cross-threading issues with the NAudio library
                Task.Factory.StartNew(() =>
                {
                    bool wasPaused = this.PlaybackState.First() == AudioPlayerState.Paused;

                    try
                    {
                        this.wavePlayer.Play();
                    }

                    catch (MmException ex)
                    {
                        throw new PlaybackException("The playback couldn't be started.", ex);
                    }

                    if (!wasPaused)
                    {
                        while (this.PlaybackState.First() != AudioPlayerState.Stopped)
                        {
                            this.UpdateSongState();
                            Thread.Sleep(250);
                        }
                    }
                });

                this.EnsureState(NAudio.Wave.PlaybackState.Playing);
                this.SetPlaybackState(AudioPlayerState.Playing);
            }
        }

        public override void Stop()
        {
            lock (this.playerLock)
            {
                if (this.wavePlayer != null && this.wavePlayer.PlaybackState != NAudio.Wave.PlaybackState.Stopped)
                {
                    this.wavePlayer.Stop();

                    this.EnsureState(NAudio.Wave.PlaybackState.Stopped);
                    this.SetPlaybackState(AudioPlayerState.Stopped);
                }
            }
        }

        private static WaveChannel32 OpenMp3Stream(Stream stream)
        {
            WaveStream mp3Stream = new Mp3FileReader(stream);

            return new WaveChannel32(mp3Stream);
        }

        private static WaveChannel32 OpenWavStream(Stream stream)
        {
            WaveStream readerStream = new WaveFileReader(stream);

            if (readerStream.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
            {
                readerStream = WaveFormatConversionStream.CreatePcmStream(readerStream);
                readerStream = new BlockAlignReductionStream(readerStream);
            }

            if (readerStream.WaveFormat.BitsPerSample != 16)
            {
                var format = new WaveFormat(readerStream.WaveFormat.SampleRate, 16, readerStream.WaveFormat.Channels);
                readerStream = new WaveFormatConversionStream(format, readerStream);
            }

            return new WaveChannel32(readerStream);
        }

        private void CreateInputStream(Song song)
        {
            Stream stream = File.OpenRead(song.StreamingPath);

            switch (song.AudioType)
            {
                case AudioType.Wav:
                    this.inputStream = OpenWavStream(stream);
                    break;

                case AudioType.Mp3:
                    this.inputStream = OpenMp3Stream(stream);
                    break;

                default:
                    throw new InvalidOperationException("Unsupported audio type.");
            }

            this.inputStream.Volume = this.Volume;
        }

        private void EnsureState(PlaybackState state)
        {
            while (this.wavePlayer.PlaybackState != state)
            {
                Thread.Sleep(200);
            }
        }

        private void UpdateSongState()
        {
            if (this.CurrentTime >= this.TotalTime.First())
            {
                this.Stop();
                this.OnSongFinished();
            }
        }
    }
}