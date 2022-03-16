﻿using ICSharpCode.SharpZipLib.Zip;
using System.Collections.Concurrent;
using System.Diagnostics;
using unlib;

namespace unp4k;

internal static class Worker
{
    private static P4kFileInstance P4K;

    internal static void ProcessGameData()
    {
        Logger.SetTitle($"unp4k: Working on {Globals.P4kFile.FullName}");
        int isDecompressableCount = 0;
        int isLockedCount = 0;
        long bytesSize = 0L;
        bool additionalFiles = false;
        P4K = new(Globals.P4kFile);

        // Setup the stream from the Data.p4k and contain it as an ICSC ZipFile with the appropriate keys then enqueue all zip entries.
        Logger.RunProgressBarAction("Processing Data.p4k, this may take a moment", () =>
        {
            // Filter out zip entries which cannot be decompressed and/or are locked behind a cypher.
            // Speed up the extraction by a large amount by filtering out the files which already exist and dont need updating.
            P4K.FilterEntries(entry =>
            {
                if (Globals.Filters.Contains("*.*") || Globals.Filters.Any(o => entry.Name.Contains(o)))
                {
                    FileInfo f = new(Path.Join(Globals.OutDirectory.FullName, entry.Name));
                    bool isDecompressable = entry.CanDecompress;
                    bool isLocked = entry.IsCrypted;
                    bool fileExists = f.Exists;
                    long fileLength = fileExists ? f.Length : 0L;
                    long entryLength = entry.Size;
                    if (fileExists && !Globals.ShouldOverwrite && !Globals.ShouldDeleteOutput)
                    {
                        additionalFiles = true;
                        if (bytesSize - fileLength > 0L) bytesSize -= fileLength;
                        else bytesSize = 0L;
                    }
                    else
                    {
                        bytesSize += entryLength;
                        if (!isDecompressable) isDecompressableCount++;
                        if (isLocked) isLockedCount++;
                    }
                    return isDecompressable && !isLocked && (Globals.ShouldOverwrite || Globals.ShouldDeleteOutput || !fileExists || fileLength != entryLength);
                }
                else return false;
            });
            P4K.OrderBy(x => x.Name);
        });

        DriveInfo outputDrive = DriveInfo.GetDrives().First(x => OS.IsWindows ? x.Name == Globals.OutDirectory.FullName[..3] : new DirectoryInfo(x.Name).Exists);
        string summary =
                @"                  \" + '\n' +
                $"                   |                     Output Path | {Globals.OutDirectory.FullName}" + '\n' +
                $"                   |                       Partition | {outputDrive.Name}" + '\n' +
                $"                   |      Partition Total Free Space | {outputDrive.TotalFreeSpace / 1000000000D:#,##0.000000000} GB" + '\n' +
                $"                   |  Partition Available Free Space | {outputDrive.AvailableFreeSpace / 1000000000D:#,##0.000000000} GB" + '\n' +
                $"                   |        Estimated Required Space | {(!Globals.ShouldOverwrite && additionalFiles ? "An Additional " : string.Empty)}" +
                                                                                $"{bytesSize / 1000000000D:#,##0.000000000} GB" + '\n' +
                 "                   |                                 | " + '\n' +
                $"                   |                      File Count | {P4K.EntryCount:#,##0}" +
                                                                                $"{(!Globals.ShouldOverwrite && additionalFiles ? " Additional Files" : string.Empty)}" +
                                                                                $"{(Globals.Filters[0] != "*.*" ? $" Filtered From {string.Join(",", Globals.Filters)}" : string.Empty)}" + '\n' +
                $"                   |              Files Incompatible | {isDecompressableCount:#,##0}" +
                                                                                $"{(!Globals.ShouldOverwrite && additionalFiles ? " Additional Files" : string.Empty)}" +
                                                                                $"{(Globals.Filters[0] != "*.*" ? $" Filtered From {string.Join(",", Globals.Filters)}" : string.Empty)}" + '\n' +
                $"                   |                    Files Locked | {isLockedCount:#,##0}" +
                                                                                $"{(!Globals.ShouldOverwrite && additionalFiles ? " Additional Files" : string.Empty)}" +
                                                                                $"{(Globals.Filters[0] != "*.*" ? $" Filtered From {string.Join(",", Globals.Filters)}" : string.Empty)}" + '\n' +
                 "                   |                                 | " + '\n' +
                $"                   |   Will Overwrite Existing Files | {Globals.ShouldOverwrite}" + '\n' +
                $"                   |    Will Delete Output Directory | {Globals.ShouldDeleteOutput}" + '\n' +
                $"                   | Will Perform Special Extraction | {Globals.ShouldForge}" + '\n' +
                @"                  /";

        // Never allow the extraction to go through if the target storage drive has too little available space.
        if (outputDrive.AvailableFreeSpace + (Globals.ShouldOverwrite || Globals.ShouldDeleteOutput ? Globals.OutDirectory.GetFiles("*.*", SearchOption.AllDirectories).Sum(x => x.Length) : 0) < bytesSize)
        {
            Logger.LogError("The output path you have chosen is on a partition which does not have enough available free space!" + '\n' + summary);
            if (!Globals.ShouldAcceptEverything) Console.ReadKey();
            Globals.InternalExitTrigger = true;
            return;
        }
        else Logger.NewLine();

        if (!Globals.ShouldAcceptEverything)
        {
            // Give the user a summary of what unp4k/unforge is about to do and some statistics.
            Logger.LogInfo("Pre-Process Summary" + '\n' + summary);
            if (!Logger.AskUserInput("Proceed?"))
            {
                Globals.InternalExitTrigger = true;
                return;
            }
        }
    }

    private static int tasksDone = 0;
    internal static void DoExtraction()
    {
        if (Globals.ShouldDeleteOutput && Globals.OutDirectory.Exists)
        {
            Logger.RunProgressBarAction($"Deleting {Globals.OutDirectory} - This may take a while", () => Globals.OutDirectory.Delete(true));
            Globals.OutDirectory.Create();
            Globals.OutForgedDirectory.Create();
        }
        Logger.ClearBuffer();

        // Time the extraction for those who are interested in it.
        Stopwatch overallTime = new();
        Stopwatch fileTime = new();
        overallTime.Start();

        // Extract each entry, then serialising it or the Forging it.
        Logger.NewLine(2);
        if (P4K.EntryCount is not 0)
        {
            byte[] decomBuffer = new byte[4096];
            BlockingCollection<ZipEntry> outputQueue = new(P4K.EntryCount);
            Task output = Task.Run(() => print(outputQueue, fileTime));
            ParallelQuery<ZipEntry> pwi = P4K.GetParallelEnumerator(Environment.ProcessorCount, ParallelMergeOptions.NotBuffered, (entry, id) =>
            {
                Logger.LogInfo($"           - Extracting: {entry.Name}");
                if (Globals.ShouldPrintDetailedLogs) fileTime.Restart();
                FileInfo extractionFile = new(Path.Join(Globals.OutDirectory.FullName, entry.Name));
                FileInfo forgeFile = new(Path.Join(Globals.OutForgedDirectory.FullName, entry.Name));
                try { P4kUnpacker.ExtractP4kEntry(P4K, entry, extractionFile, Globals.ShouldForge ? forgeFile : null); }
                catch (Exception e)
                {
                    if (Globals.ShouldPrintErrors) Logger.LogException(e);
                    if (forgeFile.Exists) forgeFile.Delete();
                    Globals.FileErrors++;
                }
                Interlocked.Increment(ref tasksDone);
                return entry;
            });

            foreach (ZipEntry item in pwi) outputQueue.Add(item);
            outputQueue.CompleteAdding();
            output.Wait();

            static void print(BlockingCollection<ZipEntry> queue, Stopwatch fileTime)
            {
                foreach (ZipEntry entry in queue.GetConsumingEnumerable())
                {
                    string percentage = (tasksDone is 0 ? 0D : 100D * tasksDone / P4K.EntryCount).ToString("000.00000");
                    if (Globals.ShouldPrintDetailedLogs)
                    {
                        Logger.LogInfo($"{percentage}% - Extracted:  {entry.Name}" + '\n' +
                            @"                              \" + '\n' +
                            $"                               | Date Last Modified: {entry.DateTime}" + '\n' +
                            $"                               | Compression Method: {entry.CompressionMethod}" + '\n' +
                            $"                               | Compressed Size:    {entry.CompressedSize  / 1000000000D:#,##0.000000000} GB" + '\n' +
                            $"                               | Uncompressed Size:  {entry.Size            / 1000000000D:#,##0.000000000} GB" + '\n' +
                            $"                               | Time Taken:         {fileTime.ElapsedMilliseconds / 1000D:#,##0.000} seconds" + '\n' +
                            @"                              /");
                    }
                    else Logger.LogInfo($"{percentage}% - Extracted:  {entry.Name[(entry.Name.LastIndexOf("/") + 1)..]}");
                }
            }
        }
        else Logger.LogInfo("No extraction work to be done!");

        // Print out the post summary.
        overallTime.Stop();
        Logger.NewLine();
        Logger.LogInfo(
            "Extraction Complete" + '\n' +
            @"\" + '\n' +
            $" |  File Errors: {Globals.FileErrors:#,##0}" + '\n' +
            $" |  Time Taken: {(float)overallTime.ElapsedMilliseconds / 60000:#,##0.000} minutes" + '\n' +
             " |  Due to the nature of SSD's/NVMe's, do not excessively (10 times a day etc) run the extraction on an SSD/NVMe. Doing so may dramatically reduce the lifetime of the SSD/NVMe.");
        if (Logger.AskUserInput("Would you like to open the output directory? (Application will close on input)")) Platform.OpenFileManager(Globals.OutDirectory.FullName);
    }
}
