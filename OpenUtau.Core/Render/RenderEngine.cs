﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.Render {
    public class Progress {
        readonly int total;
        int completed = 0;
        public Progress(int total) {
            this.total = total;
        }

        public void CompleteOne(string info) {
            Interlocked.Increment(ref completed);
            DocManager.Inst.ExecuteCmd(new ProgressBarNotification(completed * 100.0 / total, info));
        }

        public void Clear() {
            DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, string.Empty));
        }
    }

    class RenderEngine {
        readonly UProject project;
        readonly int startTick;

        public RenderEngine(UProject project, int startTick = 0) {
            this.project = project;
            this.startTick = startTick;
        }

        public Tuple<MasterAdapter, List<Fader>> RenderProject(int startTick, TaskScheduler uiScheduler, ref CancellationTokenSource cancellation) {
            var newCancellation = new CancellationTokenSource();
            var oldCancellation = Interlocked.Exchange(ref cancellation, newCancellation);
            if (oldCancellation != null) {
                oldCancellation.Cancel();
                oldCancellation.Dispose();
            }
            var faders = new List<Fader>();
            var renderTasks = new List<Tuple<RenderPhrase, WaveSource>>();
            int totalProgress = 0;
            foreach (var track in project.tracks) {
                RenderPhrase[] phrases;
                lock (project) {
                    phrases = PrepareTrack(track, project).ToArray();
                }
                var sources = phrases.Select(phrase => {
                    var firstPhone = phrase.phones.First();
                    var lastPhone = phrase.phones.Last();
                    var layout = phrase.renderer.Layout(phrase);
                    double posMs = layout.positionMs - layout.leadingMs;
                    double durMs = layout.estimatedLengthMs;
                    if (posMs + durMs < startTick * phrase.tickToMs) {
                        return null;
                    }
                    var source = new WaveSource(posMs, durMs, 0, 1);
                    renderTasks.Add(Tuple.Create(phrase, source));
                    totalProgress += phrase.phones.Length;
                    return source;
                })
                .OfType<ISignalSource>()
                .ToList();
                sources.AddRange(project.parts
                     .Where(part => part is UWavePart && part.trackNo == track.TrackNo)
                     .Select(part => part as UWavePart)
                     .Where(part => part.Samples != null)
                     .Select(part => {
                         var waveSource = new WaveSource(
                             project.TickToMillisecond(part.position),
                             project.TickToMillisecond(part.Duration),
                             part.skipMs, part.channels);
                         if (part.Samples != null) {
                             waveSource.SetSamples(part.Samples);
                         } else {
                             waveSource.SetSamples(new float[0]);
                         }
                         return (ISignalSource)waveSource;
                     }));
                var fader = new Fader(new WaveMix(sources));
                fader.Scale = PlaybackManager.DecibelToVolume(track.Mute ? -24 : track.Volume);
                fader.SetScaleToTarget();
                faders.Add(fader);
            }
            var master = new MasterAdapter(new WaveMix(faders));
            master.SetPosition((int)(project.TickToMillisecond(startTick) * 44100 / 1000) * 2);
            Task.Run(() => {
                var progress = new Progress(totalProgress);
                foreach (var renderTask in renderTasks.OrderBy(
                    task => task.Item1.position + task.Item1.phones.First().position)) {
                    if (newCancellation.IsCancellationRequested) {
                        break;
                    }
                    var task = renderTask.Item1.renderer.Render(renderTask.Item1, progress, newCancellation);
                    task.Wait();
                    renderTask.Item2.SetSamples(task.Result.samples);
                }
                progress.Clear();
            }).ContinueWith(task => {
                if (task.IsFaulted) {
                    Log.Error(task.Exception, "Failed to render.");
                    DocManager.Inst.ExecuteCmd(new UserMessageNotification(task.Exception.Flatten().Message));
                    throw task.Exception;
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, uiScheduler);
            return Tuple.Create(master, faders);
        }

        public List<WaveMix> RenderTracks(ref CancellationTokenSource cancellation) {
            var newCancellation = new CancellationTokenSource();
            var oldCancellation = Interlocked.Exchange(ref cancellation, newCancellation);
            if (oldCancellation != null) {
                oldCancellation.Cancel();
                oldCancellation.Dispose();
            }
            var result = new List<WaveMix>();
            foreach (var track in project.tracks) {
                RenderPhrase[] phrases;
                lock (project) {
                    phrases = PrepareTrack(track, project).ToArray();
                }
                var progress = new Progress(phrases.Sum(phrase => phrase.phones.Length));
                var mix = new WaveMix(phrases.Select(phrase => {
                    var task = phrase.renderer.Render(phrase, progress, newCancellation);
                    task.Wait();
                    float durMs = task.Result.samples.Length * 1000f / 44100f;
                    var source = new WaveSource(task.Result.positionMs - task.Result.leadingMs, durMs, 0, 1);
                    source.SetSamples(task.Result.samples);
                    return source;
                }));
                progress.Clear();
                result.Add(mix);
            }
            return result;
        }

        public void PreRenderProject(ref CancellationTokenSource cancellation) {
            var newCancellation = new CancellationTokenSource();
            var oldCancellation = Interlocked.Exchange(ref cancellation, newCancellation);
            if (oldCancellation != null) {
                oldCancellation.Cancel();
                oldCancellation.Dispose();
            }
            Task.Run(() => {
                try {
                    Thread.Sleep(200);
                    if (newCancellation.Token.IsCancellationRequested) {
                        return;
                    }
                    RenderPhrase[] phrases;
                    lock (project) {
                        phrases = PrepareProject(project).ToArray();
                    }
                    phrases = phrases.Where(phrase => {
                        var last = phrase.phones.Last();
                        var endPos = phrase.position + last.position + last.duration;
                        return startTick < endPos;
                    }).ToArray();
                    if (phrases.Length == 0) {
                        return;
                    }
                    var progress = new Progress(phrases.Sum(phrase => phrase.phones.Length));
                    foreach (var phrase in phrases) {
                        var task = phrase.renderer.Render(phrase, progress, newCancellation, true);
                        task.Wait();
                        var samples = task.Result;
                    }
                    progress.Clear();
                } catch (Exception e) {
                    if (!newCancellation.IsCancellationRequested) {
                        Log.Error(e, "Failed to pre-render.");
                    }
                }
            });
        }

        IEnumerable<RenderPhrase> PrepareProject(UProject project) {
            return project.tracks
                .SelectMany(track => PrepareTrack(track, project));
        }

        IEnumerable<RenderPhrase> PrepareTrack(UTrack track, UProject project) {
            return project.parts
                .Where(part => part.trackNo == track.TrackNo)
                .Where(part => part is UVoicePart)
                .Select(part => part as UVoicePart)
                .SelectMany(part => RenderPhrase.FromPart(project, project.tracks[part.trackNo], part));
        }

        public static void ReleaseSourceTemp() {
            Classic.VoicebankFiles.ReleaseSourceTemp();
        }
    }
}
