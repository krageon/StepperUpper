﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using AirBreather;
using AirBreather.IO;

using BethFile;
using BethFile.Editor;

namespace StepperUpper
{
    internal static class Cleaner
    {
        internal static Task DoCleaningAsync(IEnumerable<PluginForCleaning> plugins)
        {
            var parentTasks = new Dictionary<string, TaskCompletionSource<Merged>>();
            var pluginsMap = new Dictionary<string, PluginForCleaning>();
            foreach (var plugin in plugins)
            {
                pluginsMap.Add(plugin.Name, plugin);
                parentTasks.Add(plugin.Name, new TaskCompletionSource<Merged>());
            }

            var parents = new Dictionary<string, Merged>();
            var children = new Dictionary<string, List<Tuple<Merged, int, string>>>();

            foreach (var kvp in pluginsMap)
            {
                var pluginName = kvp.Key;
                var plugin = kvp.Value;

                if ((plugin.ParentNames.Length | plugin.RecordsToUDR.Length) == 0)
                {
                    continue;
                }

                Merged parent = parents[pluginName] = new Merged(plugin.ParentNames.Length);
                for (int i = 0; i < plugin.ParentNames.Length; i++)
                {
                    var parentName = plugin.ParentNames[i];
                    var parentPlugin = pluginsMap[parentName];
                    List<Tuple<Merged, int, string>> childrenList;
                    if (!children.TryGetValue(parentName, out childrenList))
                    {
                        childrenList = children[parentName] = new List<Tuple<Merged, int, string>>();
                    }

                    childrenList.Add(Tuple.Create(parent, i, pluginName));
                }
            }

            var cleans = new Dictionary<string, Task>(pluginsMap.Count);
            foreach (var kvp in pluginsMap)
            {
                var pluginName = kvp.Key;
                var plugin = kvp.Value;
                cleans.Add(pluginName, Task.Run(async () =>
                {
                    var parentTask = parentTasks[pluginName];
                    if (!parents.ContainsKey(pluginName))
                    {
                        parentTask.TrySetResult(default(Merged));
                    }

                    var root = await CleanPluginAsync(plugin, parentTask.Task).ConfigureAwait(false);
                    var saver = (plugin.RecordsToDelete.Length | plugin.RecordsToUDR.Length) == 0
                        ? Task.CompletedTask
                        : SavePluginAsync(root, plugin.OutputFilePath);

                    List<Tuple<Merged, int, string>> childrenList;
                    if (children.TryGetValue(pluginName, out childrenList))
                    {
                        var records = Doer.FindRecords(root).ToDictionary(r => r.Id);
                        foreach (var child in childrenList)
                        {
                            var merged = child.Item1;
                            var idx = child.Item2;
                            var childName = child.Item3;
                            merged.SetRoot(idx, records);
                            if (merged.IsFinalized)
                            {
                                parentTasks[childName].TrySetResult(merged);
                            }
                        }
                    }

                    await saver.ConfigureAwait(false);
                }));
            }

            return Task.WhenAll(cleans.Values);
        }

        private static async Task<Record> CleanPluginAsync(PluginForCleaning plugin, Task<Merged> parentTask)
        {
            Record root;
            using (var fl = AsyncFile.OpenReadSequential(plugin.DirtyFile.FullName))
            {
                root = new Record(await new BethesdaFileReader(fl).ReadFileAsync().ConfigureAwait(false));
            }

            var deletes = plugin.RecordsToDelete.ToHashSet();
            var udrs = plugin.RecordsToUDR;
            foreach (var udr in udrs)
            {
                deletes.Add(udr);
            }

            Doer.PerformDeletes(root, deletes);
            Doer.PerformUDRs(root, await parentTask.ConfigureAwait(false), udrs);
            foreach (var fieldToDelete in plugin.FieldsToDelete)
            {
                Doer.DeleteField(root, fieldToDelete.RecordId, fieldToDelete.FieldType);
            }

            return root;
        }

        private static async Task SavePluginAsync(Record root, string path)
        {
            using (var fl = AsyncFile.CreateSequential(path))
            {
                await new BethesdaFileWriter(fl).WriteAsync(Saver.Save(root)).ConfigureAwait(false);
            }
        }

        // anything where RecordsToDelete and RecordToUDR are both empty, the
        // "cleaning" process is a no-op and it just gets directly added.
        // other records have to wait for all their "parents" for part of their
        // own cleaning process, but some of it can start right away.
        internal sealed class PluginForCleaning
        {
            internal PluginForCleaning(string name, string outputFilePath, FileInfo dirtyFile, IEnumerable<string> parentNames, IEnumerable<uint> recordsToDelete, IEnumerable<uint> recordsToUDR, IEnumerable<FieldToDelete> fieldsToDelete)
            {
                this.Name = name;
                this.OutputFilePath = outputFilePath;
                this.DirtyFile = dirtyFile;
                this.ParentNames = parentNames.ToImmutableArray();
                this.RecordsToDelete = recordsToDelete.ToImmutableArray();
                this.RecordsToUDR = recordsToUDR.ToImmutableArray();
                this.FieldsToDelete = fieldsToDelete.ToImmutableArray();
            }

            internal string Name { get; }

            internal string OutputFilePath { get; }

            internal FileInfo DirtyFile { get; }

            internal ImmutableArray<string> ParentNames { get; }

            internal ImmutableArray<uint> RecordsToDelete { get; }

            internal ImmutableArray<uint> RecordsToUDR { get; }

            internal ImmutableArray<FieldToDelete> FieldsToDelete { get; }
        }

        [StructLayout(LayoutKind.Auto)]
        internal struct FieldToDelete
        {
            internal FieldToDelete(uint recordId, B4S fieldType)
            {
                this.RecordId = recordId;
                this.FieldType = fieldType;
            }

            internal uint RecordId { get; }

            internal B4S FieldType { get; }
        }
    }
}