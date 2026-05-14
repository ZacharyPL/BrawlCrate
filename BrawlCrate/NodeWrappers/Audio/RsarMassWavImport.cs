using BrawlCrate.UI;
using BrawlLib.Internal.Audio;
using BrawlLib.Internal.IO;
using BrawlLib.Internal.Windows.Forms;
using BrawlLib.SSBB.ResourceNodes;
using BrawlLib.SSBB.Types.Audio;
using BrawlLib.Wii.Audio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace BrawlCrate.NodeWrappers
{
    /// <summary>
    /// Bulk-import WAV files into RWSD/RBNK/RWAR wave lists by matching each file basename to an existing entry name,
    /// or under an RSAR folder to RSARSoundNode names (revo_kart-style SYMB trees).
    /// For RSAR folders, only sounds under the right-clicked folder are used; underscores in WAV names map to
    /// that folder relative path segment-by-segment (e.g. ATK_END.wav ↔ …/ATK/END only). Plain ATK.wav matches only …/ATK.
    /// Uses the same ADPCM encoders as the single-file Replace dialog, without prompting per file.
    /// </summary>
    internal static unsafe class RsarMassWavImport
    {
        internal const string WavFilter = "PCM Audio (*.wav)|*.wav";

        /// <summary>
        /// Every RSARSoundNode under <paramref name="root"/> that participates in name matching.
        /// </summary>
        internal static IEnumerable<RSARSoundNode> EnumerateWaveSoundsUnderFolder(RSARFolderNode root)
        {
            foreach (ResourceNode child in root.Children)
            {
                switch (child)
                {
                    case RSARFolderNode sub:
                        foreach (RSARSoundNode s in EnumerateWaveSoundsUnderFolder(sub))
                        {
                            yield return s;
                        }

                        break;
                    case RSARSoundNode snd:
                        yield return snd;
                        break;
                }
            }
        }

        /// <summary>
        /// Replace wave data for RSAR sound entries under a folder tree whose names match selected WAV files.
        /// </summary>
        internal static void RunFromRsarFolderSubtree(RSARFolderNode folder, IEnumerable<string> wavPaths)
        {
            Form owner = MainForm.Instance;
            string[] paths = wavPaths?.Where(File.Exists).ToArray() ?? Array.Empty<string>();
            if (paths.Length == 0)
            {
                return;
            }

            Dictionary<string, List<RSARSoundNode>> index =
                BuildRsarFolderSoundIndex(folder, out int skippedNoRwssdWave);

            List<string> unmatchedFiles = new List<string>();
            List<string> failures = new List<string>();
            List<string> ambiguousFiles = new List<string>();
            int replacedWaveSamples = 0;
            int touchedSoundRefs = 0;
            int skippedSharedDeclined = 0;

            HashSet<RWSDDataNode> sharedWaveApproved = new HashSet<RWSDDataNode>();
            HashSet<RWSDDataNode> sharedWaveDeclined = new HashSet<RWSDDataNode>();

            foreach (string path in paths)
            {
                string basename = Path.GetFileNameWithoutExtension(path);
                HashSet<RSARSoundNode> candidates = ResolveCandidatesFromWavBasename(index, basename);
                if (candidates.Count == 0)
                {
                    unmatchedFiles.Add(path);
                    continue;
                }

                List<RSARFileAudioNode> distinctAudios = candidates
                    .Where(s => s._waveDataNode?.Sound != null)
                    .Select(s => s._waveDataNode.Sound)
                    .Distinct()
                    .ToList();

                if (distinctAudios.Count == 0)
                {
                    failures.Add($"{Path.GetFileName(path)}: matched RSAR entries have no wave data.");
                    continue;
                }

                if (distinctAudios.Count > 1)
                {
                    string pathsList = string.Join("; ",
                        candidates.Select(s => s.TreePath).Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                            .Take(12));
                    ambiguousFiles.Add($"{Path.GetFileName(path)} ({distinctAudios.Count} different waves): {pathsList}");
                    continue;
                }

                RSARFileAudioNode targetAudio = distinctAudios[0];
                RSARSoundNode rep = candidates.First(s => ReferenceEquals(s._waveDataNode.Sound, targetAudio));
                RWSDDataNode wdn = rep._waveDataNode;

                if (wdn._refs.Count > 1)
                {
                    if (sharedWaveDeclined.Contains(wdn))
                    {
                        continue;
                    }

                    if (!sharedWaveApproved.Contains(wdn))
                    {
                        string msg = "The following entries also use this sound:\n";
                        foreach (RSARSoundNode x in wdn._refs)
                        {
                            msg += x.TreePath + "\n";
                        }

                        msg += "\nReplace this shared wave anyway?";
                        if (MessageBox.Show(owner, msg, "Mass WAV replace",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                        {
                            skippedSharedDeclined++;
                            sharedWaveDeclined.Add(wdn);
                            continue;
                        }

                        sharedWaveApproved.Add(wdn);
                    }
                }

                try
                {
                    ReplaceAudioEntryFromWav(owner, targetAudio, path);
                    replacedWaveSamples++;
                    touchedSoundRefs += wdn._refs.Count;
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(path)} ({rep.TreePath}): {ex.Message}");
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Only RSAR sounds under the folder you right-clicked were considered.");
            sb.AppendLine(
                $"Replaced {replacedWaveSamples} embedded wave sample(s), affecting {touchedSoundRefs} RSAR sound reference(s).");
            if (skippedSharedDeclined > 0)
            {
                sb.AppendLine($"{skippedSharedDeclined} shared-wave prompt(s) declined — those waves were skipped.");
            }

            if (skippedNoRwssdWave > 0)
            {
                sb.AppendLine()
                    .AppendLine(
                        $"{skippedNoRwssdWave} RSAR sound entry / entries under this folder had no RWSD wave link (skipped).");
            }

            if (ambiguousFiles.Count > 0)
            {
                sb.AppendLine().AppendLine(
                    "Ambiguous WAV files (matched multiple different waves — use a more specific path name, e.g. parent_child.wav):");
                foreach (string a in ambiguousFiles.Take(20))
                {
                    sb.AppendLine($"  • {a}");
                }

                if (ambiguousFiles.Count > 20)
                {
                    sb.AppendLine($"  … and {ambiguousFiles.Count - 20} more.");
                }
            }

            if (unmatchedFiles.Count > 0)
            {
                sb.AppendLine().AppendLine("WAV files with no matching RSAR sound under this folder:");
                foreach (string u in unmatchedFiles.Take(40))
                {
                    sb.AppendLine($"  • {Path.GetFileName(u)}");
                }

                if (unmatchedFiles.Count > 40)
                {
                    sb.AppendLine($"  … and {unmatchedFiles.Count - 40} more.");
                }
            }

            if (failures.Count > 0)
            {
                sb.AppendLine().AppendLine("Errors:");
                foreach (string f in failures.Take(25))
                {
                    sb.AppendLine($"  • {f}");
                }

                if (failures.Count > 25)
                {
                    sb.AppendLine($"  … and {failures.Count - 25} more.");
                }
            }

            MainForm.Instance?.resourceTree_SelectionChanged(null, null);

            MessageBox.Show(owner, sb.ToString(), "Mass WAV replace (RSAR folder)",
                MessageBoxButtons.OK,
                failures.Count > 0 || ambiguousFiles.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        /// <summary>
        /// Wave-linked RSAR entries often carry unexpected <see cref="RSARSoundNode.SoundType"/> values in shipped games;
        /// rely on RWSD linkage instead.
        /// </summary>
        private static bool CanReplaceRsarEmbeddedWave(RSARSoundNode s)
        {
            return s._soundFileNode is RWSDNode &&
                   s._waveDataNode != null &&
                   s._waveDataNode.Sound != null;
        }

        /// <summary>
        /// Builds path from <paramref name="sound"/> up to but not including <paramref name="folder"/> (parent walk).
        /// Only entries under the right-clicked folder subtree appear in the mass-replace index.
        /// </summary>
        private static bool TryGetRelativeSlashPath(RSARFolderNode folder, RSARSoundNode sound, out string relativePath)
        {
            relativePath = null;
            List<string> segments = new List<string>();
            ResourceNode cur = sound;
            while (cur != null)
            {
                if (ReferenceEquals(cur, folder))
                {
                    segments.Reverse();
                    if (segments.Count == 0)
                    {
                        return false;
                    }

                    relativePath = string.Join("/", segments);
                    return true;
                }

                segments.Add(cur.Name);
                cur = cur.Parent;
            }

            return false;
        }

        private static void IndexAdd(Dictionary<string, List<RSARSoundNode>> index, string key, RSARSoundNode s)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            if (!index.TryGetValue(key, out List<RSARSoundNode> list))
            {
                index[key] = list = new List<RSARSoundNode>();
            }

            if (!list.Contains(s))
            {
                list.Add(s);
            }
        }

        private static Dictionary<string, List<RSARSoundNode>> BuildRsarFolderSoundIndex(RSARFolderNode folder,
            out int skippedNoRwssdWave)
        {
            skippedNoRwssdWave = 0;
            Dictionary<string, List<RSARSoundNode>> index =
                new Dictionary<string, List<RSARSoundNode>>(StringComparer.OrdinalIgnoreCase);

            List<(RSARSoundNode sound, string relSlash)> eligible = new List<(RSARSoundNode, string)>();
            foreach (RSARSoundNode s in EnumerateWaveSoundsUnderFolder(folder))
            {
                if (!CanReplaceRsarEmbeddedWave(s))
                {
                    skippedNoRwssdWave++;
                    continue;
                }

                if (!TryGetRelativeSlashPath(folder, s, out string relSlash))
                {
                    continue;
                }

                eligible.Add((s, relSlash));
            }

            Dictionary<string, int> leafCounts = eligible
                .GroupBy(t => t.sound.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            foreach ((RSARSoundNode s, string relSlash) in eligible)
            {
                IndexAdd(index, relSlash.Replace('/', '_'), s);
                IndexAdd(index, relSlash.Replace('/', '-'), s);

                leafCounts.TryGetValue(s.Name, out int lc);
                if (lc == 1)
                {
                    IndexAdd(index, s.Name, s);
                }
            }

            return index;
        }

        /// <summary>
        /// Underscores in the WAV basename match the folder-relative path only (full path).
        /// No <c>_END</c> stripping — <c>ATK_END.wav</c> maps only to <c>ATK/END</c>, not to <c>ATK</c>.
        /// </summary>
        private static HashSet<RSARSoundNode> ResolveCandidatesFromWavBasename(
            Dictionary<string, List<RSARSoundNode>> index,
            string wavBasename)
        {
            HashSet<RSARSoundNode> result = new HashSet<RSARSoundNode>();
            void Try(string k)
            {
                if (string.IsNullOrEmpty(k))
                {
                    return;
                }

                if (index.TryGetValue(k, out List<RSARSoundNode> list))
                {
                    foreach (RSARSoundNode n in list)
                    {
                        result.Add(n);
                    }
                }
            }

            void TryBasenameVariants(string b)
            {
                if (string.IsNullOrEmpty(b))
                {
                    return;
                }

                Try(b);
                if (b.IndexOf('_') >= 0)
                {
                    Try(b.Replace('_', '-'));
                }
            }

            TryBasenameVariants(wavBasename);

            Match m = Regex.Match(wavBasename, @"^(.*)_(\d+)$");
            if (m.Success)
            {
                TryBasenameVariants(m.Groups[1].Value);
            }

            return result;
        }

        internal static void RunFromPaths(ResourceNode audioContainer, IEnumerable<string> wavPaths)
        {
            Form owner = MainForm.Instance;
            string[] paths = wavPaths?.Where(File.Exists).ToArray() ?? Array.Empty<string>();
            if (paths.Length == 0)
            {
                return;
            }

            RSARFileAudioNode[] targets = audioContainer.Children.OfType<RSARFileAudioNode>().ToArray();
            Dictionary<string, RSARFileAudioNode> byName =
                new Dictionary<string, RSARFileAudioNode>(StringComparer.OrdinalIgnoreCase);
            foreach (RSARFileAudioNode n in targets)
            {
                if (!byName.ContainsKey(n.Name))
                {
                    byName[n.Name] = n;
                }
            }

            List<string> unmatchedFiles = new List<string>();
            List<string> failures = new List<string>();
            HashSet<string> replacedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                string key = Path.GetFileNameWithoutExtension(path);
                if (!byName.TryGetValue(key, out RSARFileAudioNode node))
                {
                    unmatchedFiles.Add(path);
                    continue;
                }

                try
                {
                    ReplaceAudioEntryFromWav(owner, node, path);
                    replacedNames.Add(node.Name);
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Imported {replacedNames.Count} sound(s).");
            if (unmatchedFiles.Count > 0)
            {
                sb.AppendLine().AppendLine("WAV files with no matching entry name:");
                foreach (string u in unmatchedFiles.Take(40))
                {
                    sb.AppendLine($"  • {Path.GetFileName(u)}");
                }

                if (unmatchedFiles.Count > 40)
                {
                    sb.AppendLine($"  … and {unmatchedFiles.Count - 40} more.");
                }
            }

            if (failures.Count > 0)
            {
                sb.AppendLine().AppendLine("Errors:");
                foreach (string f in failures.Take(25))
                {
                    sb.AppendLine($"  • {f}");
                }

                if (failures.Count > 25)
                {
                    sb.AppendLine($"  … and {failures.Count - 25} more.");
                }
            }

            MessageBox.Show(owner, sb.ToString(), "Mass WAV import",
                MessageBoxButtons.OK,
                failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        /// <summary>
        /// Writes WAV into an embedded RSAR/RWSD/RBNK/RWAR PCM wave entry using RSARWaveConverter or RWAVConverter.
        /// </summary>
        internal static void ReplaceAudioEntryFromWav(Form owner, RSARFileAudioNode node, string wavPath)
        {
            using (IAudioStream stream = WAV.FromFile(wavPath))
            using (ProgressWindow progress =
                   new ProgressWindow(owner, "Wave Import", $"Encoding {Path.GetFileName(wavPath)}...", false))
            {
                FileMap audioData = node is RWAVNode
                    ? RWAVConverter.Encode(stream, progress)
                    : RSARWaveConverter.Encode(stream, progress);

                node.ReplaceRaw(audioData);
            }

            if (node is WAVESoundNode waveNode)
            {
                WaveInfo* wi = (WaveInfo*) waveNode.WorkingUncompressed.Address;
                waveNode.Init(waveNode.WorkingUncompressed.Address + wi->_dataLocation,
                    (int) (waveNode.WorkingUncompressed.Length - wi->_dataLocation), wi);
                waveNode.SetSizeInternal((int) wi->_dataLocation);
            }

            node.UpdateCurrentControl();
            node.SignalPropertyChange();
            node.Parent.Parent.SignalPropertyChange();
            ResourceNode walk = node.Parent;
            while (walk != null)
            {
                if (walk is RSARNode rsar)
                {
                    rsar.SignalPropertyChange();
                    break;
                }

                walk = walk.Parent;
            }
        }
    }
}
