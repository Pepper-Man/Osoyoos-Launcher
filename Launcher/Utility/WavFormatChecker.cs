using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ToolkitLauncher.Utility
{
    internal class WavFormatChecker
    {
        private readonly record struct AudioData
        (
            short AudioFormat,
            short Channels,
            int SampleRate,
            short Bits
        );

        private static readonly HashSet<AudioData> Gen1ValidFormats =
        [
            new(1, 1, 22050, 16),
            new(1, 1, 44100, 16),
            new(1, 2, 22050, 16),
            new(1, 2, 44100, 16)
        ];

        private static readonly HashSet<AudioData> Gen2ValidFormats =
        [
            new(1, 1, 22050, 16),
            new(1, 1, 32000, 16),
            new(1, 1, 44100, 16),
            new(1, 1, 48000, 16),
            new(1, 2, 22050, 16),
            new(1, 2, 32000, 16),
            new(1, 2, 44100, 16),
            new(1, 2, 48000, 16),
        ];

        private static readonly HashSet<string> LanguageSoundClasses =
        [
            "unit_dialog",
            "mission_dialog",
            "cinematic_dialog",
            "multiplayer_dialog",
            "cortana_mission",
            "cortana_cinematic",
            "cortana_gravemind_channel",
            "player_voice_team",
            "player_voice_proxy",
            "multilingual_test"
        ];

        private static AudioData? ReadWavHeader(string filePath)
        {
            try
            {
                using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using BinaryReader reader = new(file);

                reader.ReadBytes(4); // Chunk Id = RIFF
                int riffChunkSize = reader.ReadInt32();
                string waveFormat = Encoding.ASCII.GetString(reader.ReadBytes(4));

                // Don't even bother if we aren't WAVE format
                if (!waveFormat.Equals("WAVE", StringComparison.OrdinalIgnoreCase)) return null;

                // Find the FMT chunk - should be next in most cases, but not guaranteed
                bool foundFmt = false;
                while (file.Position < file.Length - 8)
                {
                    string chunkName = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    int chunkSize = reader.ReadInt32();

                    if (chunkName.Equals("fmt ", StringComparison.OrdinalIgnoreCase))
                    {
                        foundFmt = true;
                        break;
                    }

                    // Skip chunk payload and handle odd byte padding
                    int pad = (chunkSize % 2 != 0) ? 1 : 0;
                    file.Seek(chunkSize + pad, SeekOrigin.Current);
                }

                if (!foundFmt) return null;

                // We are now inside the FMT chunk
                short audioFormat = reader.ReadInt16();     // PCM = 1
                short channels = reader.ReadInt16();        // Mono = 1, Stereo = 2
                int sampleRate = reader.ReadInt32();        // Sample rate in Hz
                reader.ReadInt32();                         // Skip byte rate data
                reader.ReadInt16();                         // Skip block align data
                short bitsPerSample = reader.ReadInt16();   // Bits per sample

                return new AudioData(audioFormat, channels, sampleRate, bitsPerSample);
            }
            catch
            {
                // Catch any unreadable, locked or malformed files
                return null;
            }
        }

        private static List<string> GetInvalidWavs(string wavFolder, Func<AudioData?, bool> validate)
        {
            if (!Directory.Exists(wavFolder)) return [];

            List<string> badWavs = [];

            foreach (string filePath in Directory.EnumerateFiles(wavFolder, "*.wav", SearchOption.AllDirectories))
            {
                AudioData? audioData = ReadWavHeader(filePath);

                if (!validate(audioData))
                {
                    badWavs.Add(filePath);
                }
            }

            return badWavs;
        }

        public static List<string> GetInvalidForGen1(string wavFolder)
        {
            return GetInvalidWavs(wavFolder, data => data.HasValue && Gen1ValidFormats.Contains(data.Value));
        }

        public static List<string> GetInvalidForGen2(string wavFolder)
        {
            return GetInvalidWavs(wavFolder, data => data.HasValue && Gen2ValidFormats.Contains(data.Value));
        }

        public static List<string> GetInvalidForGen3(string wavFolder, string soundClass)
        {
            return GetInvalidWavs(wavFolder, data =>
            {
                if (!data.HasValue) return false;

                if (LanguageSoundClasses.Contains(soundClass))
                {
                    return data.Value.AudioFormat == 1 && data.Value.Bits == 16 && data.Value.SampleRate >= 8000;
                }

                return data.Value.AudioFormat == 1 && (data.Value.Bits == 16 || data.Value.Bits == 32);
            });
        }

        public static string BuildWarningMessage(List<string> badFormatWavs, string dataDir, int engineGeneration)
        {
            string header = engineGeneration switch
            {
                1 => "Warning - the .wav files listed below are not in a supported format for Halo 1!\nPlease make sure to use 22.05KHz or 44.1KHz 16-bit PCM formatted .wav files.\n\nBad files:",
                2 => "Warning - the .wav files listed below are not in a supported format for Halo 2!\nPlease make sure to use 22.05KHz, 32KHz, 44.1KHz or 48KHz 16-bit PCM formatted .wav files.\n\nBad files:",
                _ => "Warning - the .wav files listed below are not in a supported format for H3-Reach!\nPlease make sure to use 16-bit or 32-bit PCM formatted .wav files for SFX imports.\nFor language/dialog imports (e.g. \"unit_dialog\", \"mission_dialog\" etc.), you must use 16-bit PCM wavs with a sample rate of at least 8000Hz, otherwise lipsync data will not be generated.\n\nBad files:"
            };

            StringBuilder sb = new();
            sb.AppendLine(header);

            foreach (string wavPath in badFormatWavs)
            {
                sb.AppendLine($"\"{Path.GetRelativePath(dataDir, wavPath)}\"");
            }

            sb.AppendLine();
            sb.Append("Press OK to continue with import anyway, or Cancel to stop the import.");

            return sb.ToString();
        }
    }
}
