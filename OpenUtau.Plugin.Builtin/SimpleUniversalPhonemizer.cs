using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Plugin.Builtin {
    [Phonemizer("SUP", "Universal", "Heiden.BZR")]
    public class SimpleUniversalPhonemizer : Phonemizer {

        public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevs) {
            var note = notes[0];
            if (!config.isLoaded) {
                return new Result() {
                    phonemes = new Phoneme[]
                    {
                        new Phoneme() {
                            phoneme = note.lyric,
                            position = note.position
                        }
                    }
                };
            }

            var symbols = GetSymbols(note);
            if (prevNeighbour == null) {
                prevNoteSymbolsCache = new string[0];
            }
            var cv = config.GetCV(symbols);
            var prevLastConsonants = config.GetRightConsonants(prevNoteSymbolsCache);
            var result = TryBest(note, cv, prevLastConsonants);
            ValidatePositions(result, note, prevNeighbour);

            prevNoteSymbolsCache = symbols;
            return result;
        }

        public override void SetSinger(USinger singer) {
            this.config = new SimpleUniversalVoicebankConfiguration(singer);
            this.prevNoteSymbolsCache = new string[0];
            try {
                ReadVbConfig();
            } catch (Exception e) {
                Log.Error(e, "Failed to read config.");
            }
        }

        private SimpleUniversalVoicebankConfiguration config;
        private string[] prevNoteSymbolsCache;

        private void ReadVbConfig() {
            if (config.singer.OtherInfo.Contains("type=")) {
                ReadAtlas();
                return;
            }
            var presampPath = Path.Combine(config.singer.Location, "presamp.ini");
            if (File.Exists(presampPath)) {
                ReadPresampIni(presampPath);
                return;
            }
        }

        private void ReadAtlas() {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string atlasType = null;
            foreach (var line in config.singer.OtherInfo.Split("\n")) {
                if (line.StartsWith("type=")) {
                    atlasType = line.Substring("type=".Length);
                    break;
                }
            }
            if (atlasType == null) {
                Log.Error("Failed to read atlas type.");
                return;
            }
            var atlasPath = Path.Combine(root, "atlas", atlasType + ".atlas");
            if (!File.Exists(atlasPath)) {
                Log.Error($"Cannot find atlas file: {atlasPath}");
                return;
            }
            Log.Information($"Start reading atlas '{atlasType}'");
            foreach (var line in File.ReadAllLines(atlasPath)) {
                if (line.StartsWith("Vowels")) {
                    config.vowels = line.Substring("Vowels=".Length).Trim(',').Split(',');
                }
                else if (line.StartsWith("Consonants")) {
                    config.consonants = line.Substring("Consonants=".Length).Trim(',').Split(',');
                }
                if (config.vowels != null && config.consonants != null) {
                    break;
                }
            }
            if (config.vowels != null && config.consonants != null) {
                Log.Information($"Successfully readed '{atlasType}'");
                config.ReadSyntax();
            }
            else {
                Log.Error($"Failed to read '{atlasType}'");
            }
        }

        private void ReadPresampIni(string presampIniPath) {
            Log.Information($"Start reading presamp.ini '{presampIniPath}'");
            var isReadingVowels = false;
            var isReadingConsonants = false;
            var consonants = new List<string>();
            var vowels = new List<string>();
            foreach (var line in File.ReadAllLines(presampIniPath)) {
                if (line.StartsWith("[VOWEL]")) {
                    isReadingVowels = true;
                    isReadingConsonants = false;
                }
                else if (line.StartsWith("[CONSONANT]")) {
                    isReadingVowels = false;
                    isReadingConsonants = true;
                }
                else if (line.StartsWith("[")) {
                    isReadingVowels = false;
                    isReadingConsonants = false;
                }
                else if (isReadingConsonants) {
                    consonants.Add(line.Substring(0, line.IndexOf("=")));
                }
                else if (isReadingVowels) {
                    vowels.Add(line.Substring(0, line.IndexOf("=")));
                }
                else if (consonants != null && vowels != null) {
                    config.consonants = consonants.ToArray();
                    config.vowels = vowels.ToArray();
                    break;
                }
            }
            if (config.vowels != null && config.consonants != null) {
                Log.Information($"Successfully readed presamp.ini");
                config.ReadSyntax();
            } else {
                Log.Error($"Failed to read presamp.ini");
            }
        }

        private string[] GetSymbols(Note note) {
            // dictionary is not yet supported so just read all lyrics as phonetic input
            if (note.lyric == null) {
                return new string[0];
            } else return note.lyric.Split(" ");
        }

        private Result TryBest(Note note, string[] cv, string[] tail) {

            if (tail.All(n => config.consonants.Contains(n))) {
                cv = tail.Concat(cv).ToArray();
                tail = new string[0];
            }

            Result? result = null;
            (var prevV, var cc, var v) = config.Normalize(cv, tail);
            var joinedCc = config.JoinCc(cc);

            if (tail.Length == 0) {
                result = TryRCV(note, joinedCc, v);
                if (result.HasValue)
                    return result.Value;
            }
            else {
                result = TryVcv(note, prevV, joinedCc, v);
                if (result.HasValue)
                    return result.Value;


                result = TryCvvc(note, prevV, joinedCc, v);
                if (result.HasValue)
                    return result.Value;

                result = TryVccv(note, prevV, joinedCc, v);
                if (result.HasValue)
                    return result.Value;

                result = TryCvc(note, prevV, cc, v);
                if (result.HasValue)
                    return result.Value;
            }

            return new Result() {
                phonemes = new Phoneme[] {
                    new Phoneme() {
                        phoneme = note.lyric,
                        position = 0 //note.position
                    }
                }
            };
        }

        private Result? TryVcv(Note note, string prevV, string cc, string v) {
            var result = TryMakeAlias($"{prevV}{cc}{v}", note);
            if (result.HasValue)
                return result;

            result = TryMakeAlias($"{prevV} {cc}{v}", note);
            if (result.HasValue)
                return result;

            result = TryMakeAlias($"{prevV} {cc} {v}", note);
            if (result.HasValue)
                return result;

            return null;
        }

        private Result? TryRCV(Note note, string cc, string v) {
            var result = TryMakeAlias($"- {cc}{v}", note);
            if (result.HasValue)
                return result;

            result = TryMakeAlias($"-{cc}{v}", note);
            if (result.HasValue)
                return result;

            result = TryMakeAlias($"- {cc} {v}", note);
            if (result.HasValue)
                return result;

            return TryCV(note, cc, v);
        }

        private Result? TryCvvc(Note note, string prevV, string cc, string v) {
            return null; // not supported
            var result = TryCV(note, cc, v);
            if (!result.HasValue) {
                return null;
            }
        }

        private Result? TryVccv(Note note, string prevV, string cc, string v) {
            return null; // not supported

        }

        private Result? TryCvc(Note note, string prevV, string[] cc, string v) {
            var lastC = cc[cc.Length - 1];
            Result? result = cc.Length > 1 ? TryRCV(note, lastC, v) : TryCV(note, lastC, v);
            if (!result.HasValue) {
                return null;
            }
            Phoneme? vc = cc.Length > 1 ? TryVcr(note, v, cc[0]) : TryVc(note, v, cc[0]);
            if (!vc.HasValue) {
                return result;
            }
            var phonemes = new List<Phoneme>();
            phonemes.Add(vc.Value);
            for (var i = 1; i < cc.Length - 1; i++) {
                phonemes.Add(MakePhoneme(cc[i], note));
            };
            phonemes.Add(result.Value.phonemes[0]);
            result = new Result() {
                phonemes = phonemes.ToArray()
            };
            return result;
        }

        private Result? TryCV(Note note, string cc, string v) {
            var result = TryMakeAlias($"{cc} {v}", note);
            if (result.HasValue)
                return result;

            result = TryMakeAlias($"{cc}{v}", note);
            if (result.HasValue)
                return result;

            return null;
        }

        private Phoneme? TryVc(Note note, string v, string c) {
            var result = TryMakePhoneme($"{v} {c}", note);
            if (result.HasValue)
                return result;

            result = TryMakePhoneme($"{v}{c}", note);
            if (result.HasValue)
                return result;

            return null;
        }

        private Phoneme? TryVcr(Note note, string v, string c) {
            var result = TryMakePhoneme($"{v}{c} -", note);
            if (result.HasValue)
                return result;

            result = TryMakePhoneme($"{v}{c}-", note);
            if (result.HasValue)
                return result;

            result = TryMakePhoneme($"{v}{c} -", note);
            if (result.HasValue)
                return result;

            return null;
        }

        private Result? TryMakeAlias(string alias, Note note) {
            if (config.singer.TryGetMappedOto(alias, note.tone, out var oto)) {
                return new Result() {
                    phonemes = new Phoneme[] {
                        new Phoneme() {
                            phoneme = oto.Alias,
                            position = note.position
                        }
                    },
                };
            }
            return null;
        }

        private Phoneme? TryMakePhoneme(string alias, Note note) {
            if (config.singer.TryGetMappedOto(alias, note.tone, out var oto)) {
                return new Phoneme() {
                    phoneme = oto.Alias,
                    position = note.position
                };
            }
            return null;
        }

        private Phoneme MakePhoneme(string alias, Note note) {
            if (config.singer.TryGetMappedOto(alias, note.tone, out var oto)) {
                return new Phoneme() {
                    phoneme = oto.Alias,
                    position = note.position
                };
            }
            return new Phoneme() {
                phoneme = alias,
                position = note.position
            };
        }

        private void ValidatePositions(Result result, Note note, Note? prevNeighbour) {
            if (result.phonemes.Length <= 1) {
                return;
            }
            var vcCount = result.phonemes.Length - 1;
            var noteLength = 120; // temp
            if (prevNeighbour.HasValue) {
                var minLeftOffset = prevNeighbour.Value.position + 30;
                var maxVCLength = note.position - minLeftOffset;
                if (maxVCLength < noteLength * vcCount) {
                    noteLength = (maxVCLength / vcCount) / 15 * 15;
                }
            }
            for (var i = 0; i  < result.phonemes.Length; i++) {
                result.phonemes[result.phonemes.Length - 1 - i].position = note.position - noteLength * i;
            }
        }
    }

    class SimpleUniversalVoicebankConfiguration {
        public string[] vowels;
        public string[] consonants;
        public bool isLoaded;
        public USinger singer;

        public SimpleUniversalVoicebankConfiguration(USinger singer) {
            this.singer = singer;
        }

        public void ReadSyntax() {
            isLoaded = true;
        }

        public string[] GetRightConsonants(string[] symbols) {
            if (symbols.Length == 0)
                return symbols;
            var vowelIdx = -1;
            for (var i = symbols.Length - 1; i >=0 ; i--) {
                var symbol = (string)symbols[i];
                if (vowels.Contains(symbol)) {
                    vowelIdx = i;
                    break;
                }
            }
            if (vowelIdx == -1) {
                return new string[0];
            }
            return symbols.TakeLast(symbols.Length - vowelIdx).ToArray();
        }

        public string[] GetCV(string[] symbols) {
            var vowelIdx = -1;
            for (var i = symbols.Length - 1; i >= 0; i--) {
                var symbol = (string)symbols[i];
                if (vowels.Contains(symbol)) {
                    vowelIdx = i;
                    break;
                }
            }
            if (vowelIdx == -1) {
                return symbols;
            }
            return symbols.Take(vowelIdx + 1).ToArray();
        }

        public (string, string[], string) Normalize(string[] cv, string[] tail) {
            var prevV = "";
            var v = "";
            var cc = new List<string>();
            if (tail.Length > 0) {
                var start = 0;
                if (vowels.Contains(tail[0])) {
                    prevV = tail[0];
                    start = 1;
                }
                for (var i = start; i < tail.Length; i++) {
                    cc.Add(tail[i]);
                }
            }
            if (tail.Length > 0) {
                v = cv[tail.Length - 1];
            }
            for (var i = 1; i < cv.Length - 1; i++) {
                cc.Add(cv[i]);
            }
            if (vowels.Contains(v)) {
                cv = cv.Take(cv.Length - 1).ToArray();
            }
            else {
                v = "";
            }
            
            return (prevV, cc.ToArray(), v);
        }

        public string JoinCc(string[] symbols) {
            if (!isSpacedCc.HasValue && spacedCcSearchedTimes > 0) {
                if (singer.TryGetMappedOto(string.Join(" ", symbols), 144, out _))
                    isSpacedCc = true;

                spacedCcSearchedTimes -= 1;
            }
            return (isSpacedCc.HasValue && isSpacedCc.Value) ? string.Join(" ", symbols) : string.Join("", symbols);
        }

        private bool? isSpacedCc;
        private int spacedCcSearchedTimes = 10;
    }
}
