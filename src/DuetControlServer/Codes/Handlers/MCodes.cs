﻿using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Files;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Handlers
{
    /// <summary>
    /// Static class that processes M-codes in the control server
    /// </summary>
    public static class MCodes
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Process an M-code that should be interpreted by the control server
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Result of the code if the code completed, else null</returns>
        public static async Task<Message?> Process(Commands.Code code)
        {
            if (code.IsFromFileChannel && JobProcessor.IsSimulating && code.MajorNumber is not 0 and not 1 and not 2)
            {
                // Ignore most M-codes from files in simulation mode...
                return null;
            }

            switch (code.MajorNumber)
            {
                // Stop or Unconditional stop
                // Sleep or Conditional stop
                // Program End
                case 0:
                case 1:
                case 2:
                    if (await Processor.FlushAsync(code, syncFileStreams: true))
                    {
                        // Attempt to cancel the print from any channel other than File2
                        if (code.Channel != CodeChannel.File2)
                        {
                            using (await JobProcessor.LockAsync(code.CancellationToken))
                            {
                                if (JobProcessor.IsFileSelected)
                                {
                                    // M0/M1/M2 is permitted from inside a job file, but only permitted from elsewhere if the job is already paused
                                    if (!code.IsFromFileChannel && !JobProcessor.IsPaused)
                                    {
                                        return new Message(MessageType.Error, "Pause the print before attempting to cancel it");
                                    }

                                    // Invalidate the print file and make sure no more codes are read from it
                                    JobProcessor.Cancel();
                                }
                            }
                        }

                        // Reassign the code's cancellation token to ensure M0/M1/M2 is forwarded to RRF
                        if (code.IsFromFileChannel)
                        {
                            code.ResetCancellationToken();
                        }
                        break;
                    }
                    throw new OperationCanceledException();

                // List SD card
                case 20:
                    if (await Processor.FlushAsync(code))
                    {
                        // Resolve the directory
                        if (!code.TryGetString('P', out string? virtualDirectory))
                        {
                            using (await Provider.AccessReadOnlyAsync())
                            {
                                virtualDirectory = Provider.Get.Directories.GCodes;
                            }
                        }
                        string physicalDirectory = await FilePath.ToPhysicalAsync(virtualDirectory);

                        // Make sure to stay within limits if it is a request from the firmware
                        int maxSize = -1;
                        if (code.Flags.HasFlag(CodeFlags.IsFromFirmware))
                        {
                            maxSize = Settings.MaxMessageLength;
                        }

                        // Check if JSON file lists were requested
                        int startAt = Math.Max(code.GetInt('R', 0), 0), type = code.GetInt('S', 0);
                        if (type == 2)
                        {
                            string json = FileLists.GetFiles(virtualDirectory, physicalDirectory, startAt, true, maxSize);
                            return new Message(MessageType.Success, json);
                        }
                        if (type == 3)
                        {
                            string json = FileLists.GetFileList(virtualDirectory, physicalDirectory, startAt, maxSize);
                            return new Message(MessageType.Success, json);
                        }

                        // Print standard G-code response
                        Compatibility compatibility;
                        using (await Provider.AccessReadOnlyAsync())
                        {
                            compatibility = Provider.Get.Inputs[code.Channel]?.Compatibility ?? Compatibility.RepRapFirmware;
                        }

                        StringBuilder result = new();
                        if (compatibility == Compatibility.Default || compatibility == Compatibility.RepRapFirmware)
                        {
                            result.AppendLine("GCode files:");
                        }
                        else if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                        {
                            result.AppendLine("Begin file list:");
                        }

                        int numItems = 0;
                        bool itemFound = false;
                        foreach (string file in Directory.EnumerateFileSystemEntries(physicalDirectory))
                        {
                            if (numItems++ >= startAt)
                            {
                                string filename = Path.GetFileName(file);
                                if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                                {
                                    result.AppendLine(filename);
                                }
                                else
                                {
                                    if (itemFound)
                                    {
                                        result.Append(',');
                                    }
                                    result.Append($"\"{filename}\"");
                                }
                                itemFound = true;
                            }
                        }

                        if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                        {
                            if (!itemFound)
                            {
                                result.AppendLine("NONE");
                            }
                            result.Append("End file list");
                        }

                        return new Message(MessageType.Success, result.ToString());
                    }
                    throw new OperationCanceledException();

                // Initialize SD card
                case 21:
                    if (await Processor.FlushAsync(code))
                    {
                        if (code.GetInt('P', 0) == 0)
                        {
                            // M21 (P0) will always work because it's always mounted
                            return new Message();
                        }
                        throw new NotSupportedException();
                    }
                    throw new OperationCanceledException();

                // Release SD card
                case 22:
                    throw new NotSupportedException();

                // Select a file to print
                case 23:
                case 32:
                    if (await Processor.FlushAsync(code, syncFileStreams: true))
                    {
                        if (code.Channel != CodeChannel.File2)
                        {
                            string fileName = code.GetUnprecedentedString();
                            if (string.IsNullOrWhiteSpace(fileName))
                            {
                                return new Message(MessageType.Error, "Filename expected");
                            }

                            string physicalFile = await FilePath.ToPhysicalAsync(fileName, FileDirectory.GCodes);
                            if (!File.Exists(physicalFile))
                            {
                                return new Message(MessageType.Error, $"Could not find file {fileName}");
                            }

                            using (await JobProcessor.LockAsync(code.CancellationToken))
                            {
                                if (!code.IsFromFileChannel && JobProcessor.IsProcessing)
                                {
                                    return new Message(MessageType.Error, "Cannot set file to print, because a file is already being printed");
                                }
                                await JobProcessor.SelectFile(fileName, physicalFile);
                            }
                        }

                        // Let RRF do everything else
                        break;
                    }
                    throw new OperationCanceledException();

                // Resume a file print
                case 24:
                    if (await Processor.FlushAsync(code, syncFileStreams: true))
                    {
                        if (code.Channel != CodeChannel.File2)
                        {
                            using (await JobProcessor.LockAsync(code.CancellationToken))
                            {
                                if (!JobProcessor.IsFileSelected)
                                {
                                    return new Message(MessageType.Error, "Cannot print, because no file is selected!");
                                }
                            }
                        }

                        // Let RepRapFirmware process this request so it can invoke resume.g. When M24 completes, the file is resumed
                        break;
                    }
                    throw new OperationCanceledException();

                // Set SD position
                case 26:
                    if (await Processor.FlushAsync(code))
                    {
                        // Wait for inputs[].motionSystem to be up-to-date
                        await Updater.WaitForFullUpdate();

                        int motionSystem;
                        using (await Provider.AccessReadOnlyAsync())
                        {
                            motionSystem = Provider.Get.Inputs[code.Channel]?.MotionSystem ?? 0;
                        }

                        using (await JobProcessor.LockAsync(code.CancellationToken))
                        {
                            if (!JobProcessor.IsFileSelected)
                            {
                                return new Message(MessageType.Error, "Not printing a file");
                            }

                            if (code.TryGetLong('S', out long newPosition))
                            {
                                if (newPosition < 0L || newPosition > JobProcessor.FileLength)
                                {
                                    return new Message(MessageType.Error, "Position is out of range");
                                }

                                await JobProcessor.SetFilePosition(motionSystem, newPosition);
                            }
                        }

                        // P parameter is handled by RRF if present
                        break;
                    }
                    throw new OperationCanceledException();

                // Report SD print status
                case 27:
                    if (await Processor.FlushAsync(code))
                    {
                        // Wait for inputs[].motionSystem to be up-to-date
                        await Updater.WaitForFullUpdate();
                        int motionSystem;
                        using (await Provider.AccessReadOnlyAsync())
                        {
                            motionSystem = Provider.Get.Inputs[code.Channel]?.MotionSystem ?? 0;
                        }

                        using (await JobProcessor.LockAsync(code.CancellationToken))
                        {
                            if (JobProcessor.IsFileSelected)
                            {
                                long filePosition = await JobProcessor.GetFilePosition(motionSystem);
                                return new Message(MessageType.Success, $"SD printing byte {filePosition}/{JobProcessor.FileLength}");
                            }
                            return new Message(MessageType.Success, "Not SD printing.");
                        }
                    }
                    throw new OperationCanceledException();

                // Begin write to SD card
                case 28:
                    if (await Processor.FlushAsync(code))
                    {
                        int numChannel = (int)code.Channel;
                        using (await Commands.Code.FileLocks[numChannel].LockAsync(Program.CancellationToken))
                        {
                            if (Commands.Code.FilesBeingWritten[numChannel] is not null)
                            {
                                return new Message(MessageType.Error, "Another file is already being written to");
                            }

                            string file = code.GetUnprecedentedString();
                            if (string.IsNullOrWhiteSpace(file))
                            {
                                return new Message(MessageType.Error, "Filename expected");
                            }

                            string prefix = await code.EmulatingMarlin() ? "ok\n" : string.Empty;
                            string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.GCodes);
                            try
                            {
                                FileStream fileStream = new(physicalFile, FileMode.Create, FileAccess.Write, FileShare.Read, Settings.FileBufferSize);
                                StreamWriter writer = new(fileStream, Encoding.UTF8, Settings.FileBufferSize);
                                Commands.Code.FilesBeingWritten[numChannel] = writer;
                                return new Message(MessageType.Success, prefix + $"Writing to file: {file}");
                            }
                            catch (Exception e)
                            {
                                _logger.Debug(e, "Failed to open file for writing");
                                return new Message(MessageType.Error, prefix + $"Can't open file {file} for writing.");
                            }
                        }
                    }
                    throw new OperationCanceledException();

                // End write to SD card
                case 29:
                    if (await Processor.FlushAsync(code))
                    {
                        int numChannel = (int)code.Channel;
                        using (await Commands.Code.FileLocks[numChannel].LockAsync(Program.CancellationToken))
                        {
                            StreamWriter? writer = Commands.Code.FilesBeingWritten[numChannel];
                            if (writer is not null)
                            {
                                Stream stream = writer.BaseStream;
                                await writer.DisposeAsync();
                                Commands.Code.FilesBeingWritten[numChannel] = null;
                                await stream.DisposeAsync();

                                if (await code.EmulatingMarlin())
                                {
                                    return new Message(MessageType.Success, "Done saving file.");
                                }
                                return new Message();
                            }
                            break;
                        }
                    }
                    throw new OperationCanceledException();

                // Delete a file on the SD card
                case 30:
                    if (await Processor.FlushAsync(code))
                    {
                        string file = code.GetUnprecedentedString();
                        string physicalFile = await FilePath.ToPhysicalAsync(file);

                        try
                        {
                            File.Delete(physicalFile);
                            return new Message();
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to delete file");
                            return new Message(MessageType.Error, $"Failed to delete file {file}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // For case 32, see case 23

                // Return file information
                case 36:
                    if (code.Parameters.Count > 0)
                    {
                        if (await Processor.FlushAsync(code))
                        {
                            try
                            {
                                // Get fileinfo
                                if (code.MinorNumber != 1)
                                {
                                    string file = await FilePath.ToPhysicalAsync(code.GetUnprecedentedString(), FileDirectory.GCodes);
                                    GCodeFileInfo info = await InfoParser.Parse(file, false);

                                    string json = JsonSerializer.Serialize(info, JsonHelper.DefaultJsonOptions);
                                    return new Message(MessageType.Success, "{\"err\":0," + json[1..]);
                                }

                                // Get thumbnail
                                string filename = await FilePath.ToPhysicalAsync(code.GetString('P'), FileDirectory.GCodes);
                                string thumbnailJson = await InfoParser.ParseThumbnail(filename, code.GetLong('S'));
                                return new Message(MessageType.Success, thumbnailJson);
                            }
                            catch (Exception e)
                            {
                                _logger.Debug(e, "Failed to return file information");
                                return new Message(MessageType.Success, "{\"err\":1}");
                            }
                        }
                        throw new OperationCanceledException();
                    }
                    break;

                // Simulate file
                case 37:
                    if (await Processor.FlushAsync(code, syncFileStreams: true))
                    {
                        if (code.Channel != CodeChannel.File2)
                        {
                            string fileName = code.GetString('P');
                            string physicalFile = await FilePath.ToPhysicalAsync(fileName, FileDirectory.GCodes);
                            if (!File.Exists(physicalFile))
                            {
                                return new Message(MessageType.Error, $"GCode file \"{fileName}\" not found");
                            }

                            using (await JobProcessor.LockAsync(code.CancellationToken))
                            {
                                if (!code.IsFromFileChannel && JobProcessor.IsProcessing)
                                {
                                    return new Message(MessageType.Error, "Cannot set file to simulate, because a file is already being printed");
                                }

                                await JobProcessor.SelectFile(fileName, physicalFile, true);
                                // Simulation is started when M37 has been processed by the firmware
                            }
                        }

                        // Let RRF do everything else
                        break;
                    }
                    throw new OperationCanceledException();

                // Compute SHA1 hash of target file
                case 38:
                    if (await Processor.FlushAsync(code))
                    {
                        string file = code.GetUnprecedentedString(), physicalFile = await FilePath.ToPhysicalAsync(file);
                        try
                        {
                            await using FileStream stream = new(physicalFile, FileMode.Open, FileAccess.Read, FileShare.Read, Settings.FileBufferSize);

                            using System.Security.Cryptography.SHA1 sha1 = System.Security.Cryptography.SHA1.Create();
                            byte[] hash = await Task.Run(() => sha1.ComputeHash(stream), code.CancellationToken);

                            return new Message(MessageType.Success, BitConverter.ToString(hash).Replace("-", string.Empty));
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to compute SHA1 checksum");
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException!;
                            }
                            return new Message(MessageType.Error, $"Could not compute SHA1 checksum for file {file}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Report SD card information
                case 39:
                    if (await Processor.FlushAsync(code))
                    {
                        using (await Provider.AccessReadOnlyAsync())
                        {
                            int index = code.GetInt('P', 0);
                            if (code.GetInt('S', 0) == 2)
                            {
                                if (index < 0 || index >= Provider.Get.Volumes.Count)
                                {
                                    return new Message(MessageType.Success, $"{{\"SDinfo\":{{\"slot\":{index},present:0}}}}");
                                }

                                Volume storage = Provider.Get.Volumes[index];
                                var output = new
                                {
                                    SDinfo = new
                                    {
                                        slot = index,
                                        present = 1,
                                        capacity = storage.Capacity,
                                        partitionSize = storage.PartitionSize,
                                        free = storage.FreeSpace,
                                        speed = storage.Speed
                                    }
                                };
                                return new Message(MessageType.Success, JsonSerializer.Serialize(output, JsonHelper.DefaultJsonOptions));
                            }
                            else
                            {
                                if (index < 0 || index >= Provider.Get.Volumes.Count)
                                {
                                    return new Message(MessageType.Error, $"Bad SD slot number: {index}");
                                }

                                Volume storage = Provider.Get.Volumes[index];
                                return new Message(MessageType.Success, $"SD card in slot {index}: capacity {storage.Capacity / (1000 * 1000 * 1000):F2}Gb, partition size {storage.PartitionSize / (1000 * 1000 * 1000):F2}Gb,free space {storage.FreeSpace / (1000 * 1000 * 1000):F2}Gb, speed {storage.Speed / (1000 * 1000):F2}MBytes/sec");
                            }
                        }
                    }
                    throw new OperationCanceledException();

                // Flag current macro file as (not) pausable
                case 98:
                    {
                        if (code.TryGetInt('R', out int rParam))
                        {
                            if (await Processor.FlushAsync(code))
                            {
                                await SPI.Interface.SetMacroPausable(code.Channel, rParam == 1);
                            }
                            else
                            {
                                throw new OperationCanceledException();
                            }
                        }
                        break;
                    }

                // Emergency Stop
                case 112:
                    if (code.Flags.HasFlag(CodeFlags.IsPrioritized) || await Processor.FlushAsync(code))
                    {
                        // Wait for potential firmware updates to complete first
                        await SPI.Interface.WaitForUpdateAsync();

                        // Perform emergency stop but don't wait longer than 4.5s
                        Task stopTask = SPI.Interface.EmergencyStop();
                        Task completedTask = await Task.WhenAny(stopTask, Task.Delay(4500, Program.CancellationToken));
                        if (stopTask != completedTask)
                        {
                            // Halt timed out, kill this program
                            await Program.ShutdownAsync(true);
                            return new Message(MessageType.Error, "Halt timed out, killing DCS");
                        }

                        // RRF halted
                        using (await Provider.AccessReadWriteAsync())
                        {
                            Provider.Get.State.Status = MachineStatus.Halted;
                        }
                        return new Message();
                    }
                    throw new OperationCanceledException();

                // Immediate DSF diagnostics
                case 122:
                    if (code.GetInt('B', 0) == 0 && code.GetUnprecedentedString() == "DSF")
                    {
                        Message result = new();
                        await Diagnostics(result);
                        return result;
                    }
                    break;

                // Query object model
                case 409:
                    {
                        if (code.TryGetInt('I', out int iVal) && iVal > 0)
                        {
                            return new Message(MessageType.Error, "M409 I1 is reserved for internal purposes only");
                        }

                        if (code.TryGetString('K', out string? key) && (!code.TryGetInt('R', out int rParam) || rParam == 0))
                        {
                            if (!key.TrimStart('#').StartsWith("network") && !key.TrimStart('#').StartsWith("volumes"))
                            {
                                // Only return query results for network and volume keys as part of M409
                                break;
                            }

                            // Retrieve filtered OM data. At present, flags are ignored
                            code.TryGetString('F', out string? flags);
                            using JsonDocument queryResult = JsonSerializer.SerializeToDocument(Filter.GetFiltered(key + ".**"), JsonHelper.DefaultJsonOptions);

                            // Get down to the requested depth
                            JsonElement result = queryResult.RootElement;
                            if (key is not null)
                            {
                                foreach (string depth in key.Split('.'))
                                {
                                    if (result.ValueKind == JsonValueKind.Object)
                                    {
                                        foreach (var subItem in result.EnumerateObject())
                                        {
                                            result = subItem.Value;
                                            break;
                                        }
                                    }
                                }
                            }

                            // Generate final OM response
                            object finalResult;
                            if (result.ValueKind == JsonValueKind.Array)
                            {
                                finalResult = new
                                {
                                    key,
                                    flags = flags ?? string.Empty,
                                    result,
                                    next = 0
                                };
                            }
                            else
                            {
                                finalResult = new
                                {
                                    key,
                                    flags = flags ?? string.Empty,
                                    result
                                };
                            }
                            return new Message(MessageType.Success, JsonSerializer.Serialize(finalResult, JsonHelper.DefaultJsonOptions));
                        }
                        else
                        {
                            break;
                        }
                    }

                // Create Directory on SD-Card
                case 470:
                    if (await Processor.FlushAsync(code))
                    {
                        string path = code.GetString('P'), physicalPath = await FilePath.ToPhysicalAsync(path);
                        try
                        {
                            Directory.CreateDirectory(physicalPath);
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to create directory");
                            return new Message(MessageType.Error, $"Failed to create directory {path}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Rename File/Directory on SD-Card
                case 471:
                    if (await Processor.FlushAsync(code))
                    {
                        string from = code.GetString('S'), to = code.GetString('T');
                        try
                        {
                            string source = await FilePath.ToPhysicalAsync(from), destination = await FilePath.ToPhysicalAsync(to);
                            if (File.Exists(source))
                            {
                                if (File.Exists(destination) && code.GetBool('D', false))
                                {
                                    File.Delete(destination);
                                }
                                File.Move(source, destination);
                            }
                            else if (Directory.Exists(source))
                            {
                                if (Directory.Exists(destination) && code.GetBool('D', false))
                                {
                                    // This could be recursive but at the moment we mimic RRF's behaviour
                                    Directory.Delete(destination);
                                }
                                Directory.Move(source, destination);
                            }
                            else
                            {
                                throw new FileNotFoundException();
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to rename file or directory");
                            return new Message(MessageType.Error, $"Failed to rename file or directory {from} to {to}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Delete file/directory
                case 472:
                    if (await Processor.FlushAsync(code))
                    {
                        string path = code.GetString('P'), physicalPath = await FilePath.ToPhysicalAsync(path);
                        try
                        {
                            if (Directory.Exists(physicalPath))
                            {
                                _ = code.TryGetBool('R', out bool recursive);
                                Directory.Delete(physicalPath, recursive);
                            }
                            else
                            {
                                File.Delete(physicalPath);
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to delete file or directory");
                            return new Message(MessageType.Error, $"Failed to delete file or directory {path}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Print settings
                case 503:
                    if (await Processor.FlushAsync(code))
                    {
                        string configFile = await FilePath.ToPhysicalAsync(FilePath.ConfigFile, FileDirectory.System);
                        if (File.Exists(configFile))
                        {
                            string content = await File.ReadAllTextAsync(configFile);
                            return new Message(MessageType.Success, content);
                        }

                        string configFileFallback = await FilePath.ToPhysicalAsync(FilePath.ConfigFileFallback, FileDirectory.System);
                        if (File.Exists(configFileFallback))
                        {
                            string content = await File.ReadAllTextAsync(configFileFallback);
                            return new Message(MessageType.Success, content);
                        }
                        return new Message(MessageType.Error, "Configuration file not found");
                    }
                    throw new OperationCanceledException();

                // Set configuration file folder
                case 505:
                    if (await Processor.FlushAsync(code))
                    {
                        if (code.TryGetString('P', out string? directory))
                        {
                            await using (await SPI.Interface.LockAllMovementSystemsAndWaitForStandstill(code.Channel))
                            {
                                string physicalDirectory = await FilePath.ToPhysicalAsync(directory, "sys");
                                if (Directory.Exists(physicalDirectory))
                                {
                                    string virtualDirectory = await FilePath.ToVirtualAsync(physicalDirectory);
                                    using (await Provider.AccessReadWriteAsync())
                                    {
                                        Provider.Get.Directories.System = virtualDirectory;
                                    }
                                    return new Message();
                                }
                            }
                            return new Message(MessageType.Error, "Directory not found");
                        }

                        using (await Provider.AccessReadOnlyAsync())
                        {
                            return new Message(MessageType.Success, $"Sys file path is {Provider.Get.Directories.System}");
                        }
                    }
                    throw new OperationCanceledException();

                // Set Name
                case 550:
                    if (await Processor.FlushAsync(code))
                    {
                        if (code.TryGetString('P', out string? newName))
                        {
                            if (newName.Length > 40)
                            {
                                return new Message(MessageType.Error, "Machine name is too long");
                            }

                            // Strip letters and digits from the machine name
                            string machineName = string.Empty;
                            foreach (char c in Environment.MachineName)
                            {
                                if (char.IsLetterOrDigit(c))
                                {
                                    machineName += c;
                                }
                            }

                            // Strip letters and digits from the desired name
                            string desiredName = string.Empty;
                            foreach (char c in newName)
                            {
                                if (char.IsLetterOrDigit(c))
                                {
                                    desiredName += c;
                                }
                            }

                            // Make sure the subset of letters and digits is equal
                            if (!machineName.Equals(desiredName, StringComparison.CurrentCultureIgnoreCase))
                            {
                                return new Message(MessageType.Error, "Machine name must consist of the same letters and digits as configured by the Linux hostname");
                            }

                            // Hostname is legit - pass this code on to RRF so it can update the name too
                        }
                        break;
                    }
                    throw new OperationCanceledException();

                // Set Password
                case 551:
                    if (await Processor.FlushAsync(code))
                    {
                        if (code.TryGetString('P', out string? password))
                        {
                            using (await Provider.AccessReadWriteAsync())
                            {
                                Provider.Password = password;
                            }
                        }
                        break;
                    }
                    throw new OperationCanceledException();

                // Configure network protocols
                case 586:
                    if (await Processor.FlushAsync(code))
                    {
                        if (code.TryGetString('C', out string? corsSite))
                        {
                            using (await Provider.AccessReadWriteAsync())
                            {
                                Provider.Get.Network.CorsSite = string.IsNullOrWhiteSpace(corsSite) ? null : corsSite;
                            }
                            return new Message();
                        }

                        using (await Provider.AccessReadOnlyAsync())
                        {
                            if (string.IsNullOrEmpty(Provider.Get.Network.CorsSite))
                            {
                                return new Message(MessageType.Success, "CORS disabled");
                            }
                            return new Message(MessageType.Success, $"CORS enabled for site '{Provider.Get.Network.CorsSite}'");
                        }
                    }
                    throw new OperationCanceledException();

                // Fork input reader
                case 606:
                    if (await Processor.FlushAsync(code))
                    {
                        if (code.TryGetInt('S', out int sParam) && sParam == 1)
                        {
                            using (await Provider.AccessReadOnlyAsync())
                            {
                                if (Provider.Get.Inputs[CodeChannel.File2] is null)
                                {
                                    // Command not supported. Let RRF decide what to do
                                    break;
                                }
                            }

                            // Try to fork the file and report an error if anything went wrong
                            using (await JobProcessor.LockAsync(code.CancellationToken))
                            {
                                Message result = await JobProcessor.ForkAsync(code);
                                if (result.Type != MessageType.Success)
                                {
                                    return result;
                                }
                            }
                        }

                        // Let RRF carry on
                        break;
                    }
                    throw new OperationCanceledException();

                // Set current RTC date and time
                case 905:
                    if (await Processor.FlushAsync(code))
                    {
                        bool seen = false;

                        if (code.TryGetString('P', out string? dayString))
                        {
                            if (DateTime.TryParseExact(dayString, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                            {
                                await System.Diagnostics.Process.Start("timedatectl", $"set-time {date:yyyy-MM-dd}").WaitForExitAsync(Program.CancellationToken);
                                seen = true;
                            }
                            else
                            {
                                return new Message(MessageType.Error, "Invalid date format");
                            }
                        }

                        if (code.TryGetString('S', out string? timeString))
                        {
                            if (DateTime.TryParseExact(timeString, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time))
                            {
                                await System.Diagnostics.Process.Start("timedatectl", $"set-time {time:HH:mm:ss}").WaitForExitAsync(Program.CancellationToken);
                                seen = true;
                            }
                            else
                            {
                                return new Message(MessageType.Error, "Invalid time format");
                            }
                        }

                        if (code.TryGetString('T', out string? timezone))
                        {
                            if (File.Exists($"/usr/share/zoneinfo/{timezone}"))
                            {
                                await System.Diagnostics.Process.Start("timedatectl", $"set-timezone {timezone}").WaitForExitAsync(Program.CancellationToken);
                                seen = true;
                            }
                            else
                            {
                                return new Message(MessageType.Error, "Invalid time zone");
                            }
                        }

                        if (!seen)
                        {
                            return new Message(MessageType.Success, $"Current date and time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        }
                    }
                    throw new OperationCanceledException();

                // Start/stop event logging to SD card
                case 929:
                    if (await Processor.FlushAsync(code))
                    {
                        if (!code.TryGetInt('S', out int sParam))
                        {
                            using (await Provider.AccessReadOnlyAsync())
                            {
                                if (Provider.Get.State.LogLevel == LogLevel.Off)
                                {
                                    return new Message(MessageType.Success, "Event logging is disabled");
                                }
                                return new Message(MessageType.Success, $"Event logging is enabled at log level {Provider.Get.State.LogLevel.ToString().ToLowerInvariant()}");
                            }
                        }

                        if (sParam > 0 && sParam < 4)
                        {
                            LogLevel logLevel = sParam switch
                            {
                                1 => LogLevel.Warn,
                                2 => LogLevel.Info,
                                3 => LogLevel.Debug,
                                _ => LogLevel.Off
                            };

                            string defaultLogFile = Logger.DefaultLogFile;
                            using (await Provider.AccessReadOnlyAsync())
                            {
                                if (!string.IsNullOrEmpty(Provider.Get.State.LogFile))
                                {
                                    defaultLogFile = Provider.Get.State.LogFile;
                                }
                            }

                            await Logger.StartAsync(code.GetString('P', defaultLogFile), logLevel);
                        }
                        else
                        {
                            await Logger.StopAsync();
                        }
                        return new Message();
                    }
                    throw new OperationCanceledException();

                // Update the firmware
                case 997:
                    if (code.GetIntArray('S', new[] { 0 }).Contains(0) && code.GetInt('B', 0) == 0)
                    {
                        if (await Processor.FlushAsync(code))
                        {
                            // Get the IAP and Firmware files
                            string? iapFile, firmwareFile;
                            using (await Provider.AccessReadOnlyAsync())
                            {
                                if (Provider.Get.Boards.Count == 0)
                                {
                                    return new Message(MessageType.Error, "No boards have been detected");
                                }

                                // There are now two different IAP binaries, check which one to use
                                iapFile = Provider.Get.Boards[0].IapFileNameSBC;
                                if (!code.TryGetString('P', out firmwareFile))
                                {
                                    firmwareFile = Provider.Get.Boards[0].FirmwareFileName;
                                }
                            }

                            if (string.IsNullOrEmpty(iapFile) || string.IsNullOrEmpty(firmwareFile))
                            {
                                return new Message(MessageType.Error, "Cannot update firmware because IAP and firmware filenames are unknown");
                            }

                            string physicalIapFile = await FilePath.ToPhysicalAsync(iapFile, FileDirectory.Firmware);
                            if (!File.Exists(physicalIapFile))
                            {
                                string fallbackIapFile = await FilePath.ToPhysicalAsync($"0:/firmware/{iapFile}");
                                if (!File.Exists(fallbackIapFile))
                                {
                                    fallbackIapFile = await FilePath.ToPhysicalAsync(iapFile, FileDirectory.System);
                                    if (!File.Exists(fallbackIapFile))
                                    {
                                        return new Message(MessageType.Error, $"Failed to find IAP file {iapFile}");
                                    }
                                }
                                _logger.Warn("Using fallback IAP file {0}", fallbackIapFile);
                                physicalIapFile = fallbackIapFile;
                            }

                            string physicalFirmwareFile = await FilePath.ToPhysicalAsync(firmwareFile, FileDirectory.Firmware);
                            if (!File.Exists(physicalFirmwareFile))
                            {
                                string fallbackFirmwareFile = await FilePath.ToPhysicalAsync($"0:/firmware/{firmwareFile}");
                                if (!File.Exists(fallbackFirmwareFile))
                                {
                                    fallbackFirmwareFile = await FilePath.ToPhysicalAsync(firmwareFile, FileDirectory.System);
                                    if (!File.Exists(fallbackFirmwareFile))
                                    {
                                        return new Message(MessageType.Error, $"Failed to find firmware file {firmwareFile}");
                                    }
                                }
                                _logger.Warn("Using fallback firmware file {0}", fallbackFirmwareFile);
                                physicalFirmwareFile = fallbackFirmwareFile;
                            }

                            // Stop all the plugins
                            Commands.StopPlugins stopCommand = new();
                            await stopCommand.Execute();

                            // Flash the firmware
                            await using FileStream iapStream = new(physicalIapFile, FileMode.Open, FileAccess.Read, FileShare.Read, Settings.FileBufferSize);
                            await using FileStream firmwareStream = new(physicalFirmwareFile, FileMode.Open, FileAccess.Read, FileShare.Read, Settings.FileBufferSize);
                            if (Path.GetExtension(firmwareFile) == ".uf2")
                            {
                                await using MemoryStream unpackedFirmwareStream = await Firmware.UnpackUF2(firmwareStream);
                                await SPI.Interface.UpdateFirmware(iapStream, unpackedFirmwareStream);
                            }
                            else
                            {
                                await SPI.Interface.UpdateFirmware(iapStream, firmwareStream);
                            }

                            // Terminate the program - or - restart the plugins when done
                            if (Settings.UpdateOnly || !Settings.NoTerminateOnReset)
                            {
                                _ = code.Task.ContinueWith(async task =>
                                {
                                    await task;
                                    await Program.ShutdownAsync();
                                }, TaskContinuationOptions.RunContinuationsAsynchronously);
                            }
                            else
                            {
                                await Updater.WaitForFullUpdate();

                                Commands.StartPlugins startCommand = new();
                                await startCommand.Execute();
                            }
                            return new Message();
                        }
                        throw new OperationCanceledException();
                    }
                    break;

                // Request resend of line
                case 998:
                    throw new NotSupportedException();

                // Reset controller
                case 999:
                    if (code.Parameters.Count == 0)
                    {
                        if (code.Flags.HasFlag(CodeFlags.IsPrioritized) || await Processor.FlushAsync(code))
                        {
                            // Wait for potential firmware updates to complete first
                            await SPI.Interface.WaitForUpdateAsync();

                            // Perform firmware reset but don't wait longer than 4.5s
                            Task resetTask = SPI.Interface.ResetFirmware();
                            Task completedTask = await Task.WhenAny(resetTask, Task.Delay(4500, Program.CancellationToken));
                            if (resetTask != completedTask)
                            {
                                // Reset timed out, kill this program
                                await Program.ShutdownAsync(true);
                                return new Message(MessageType.Error, "Reset timed out, killing DCS");
                            }

                            // Firmware reset
                            return new Message();
                        }
                        throw new OperationCanceledException();
                    }
                    break;
            }
            return null;
        }

        /// <summary>
        /// React to an executed M-code before its result is returned
        /// </summary>
        /// <param name="code">Code processed by RepRapFirmware</param>
        /// <returns>Result to output</returns>
        /// <remarks>This method shall be used only to update values that are time-critical. Others are supposed to be updated via the object model</remarks>
        public static async Task CodeExecuted(Commands.Code code)
        {
            if (code.Result is null || code.Result.Type != MessageType.Success)
            {
                return;
            }

            switch (code.MajorNumber)
            {
                // Stop or Unconditional stop
                // Sleep or Conditional stop
                // Resume print
                // Select file and start SD print
                // Simulate file
                case 0:
                case 1:
                case 24:
                case 32:
                case 37:
                    using (await JobProcessor.LockAsync(code.CancellationToken))
                    {
                        // Start sending file instructions to RepRapFirmware or finish the cancellation process
                        JobProcessor.Resume();
                    }
                    break;

                // Diagnostics
                case 122:
                    if (code.GetInt('B', 0) == 0 && code.GetInt('P', 0) == 0 && code.GetUnprecedentedString() != "DSF" && !string.IsNullOrEmpty(code.Result.Content))
                    {
                        // Append our own diagnostics to RRF's M122 output
                        await Diagnostics(code.Result);
                    }
                    break;

                // Pop
                case 121:
                    await Updater.WaitForFullUpdate(code.CancellationToken);      // This may change inputs[].active, so sync the OM here
                    break;

                // Query object model
                case 409:
                    if (code.HasParameter('I') && !string.IsNullOrWhiteSpace(code.Result.Content))
                    {
                        // Clear output of M409 K"..." I1 case an outdated firmware version is used with this DSF build
                        code.Result.Content = string.Empty;
                    }
                    break;

                // Select movement queue number
                case 596:
                    _logger.Debug("Requesting full model update after M596");
                    await Updater.WaitForFullUpdate(code.CancellationToken);    // This changes inputs[].active, so sync the OM here
                    _logger.Debug("Requested full model update after M596");
                    break;

                // Fork input reader
                case 606:
                    if (code.TryGetInt('S', out int sParam) && sParam == 1)
                    {
                        _logger.Debug("Requesting full model update after M606 S1");
                        await Updater.WaitForFullUpdate(code.CancellationToken);    // This changes inputs[].active, so sync the OM here
                        _logger.Debug("Requested full model update after M606 S1");

                        SPI.Channel.Processor.StartCopiedMacros();
                        using (await JobProcessor.LockAsync())
                        {
                            JobProcessor.StartSecondJob();
                        }
                    }
                    break;

                // Reset controller
                case 999:
                    if (!Settings.NoTerminateOnReset && code.Parameters.Count == 0)
                    {
                        // DCS is supposed to terminate via M999 unless this option is explicitly disabled
                        _ = code.Task.ContinueWith(async task =>
                        {
                            await task;
                            await Program.ShutdownAsync();
                        }, TaskContinuationOptions.RunContinuationsAsynchronously);
                    }
                    break;
            }
        }

        /// <summary>
        /// Print the diagnostics
        /// </summary>
        /// <param name="result">Target to write to</param>
        /// <returns>Asynchronous task</returns>
        private static async Task Diagnostics(Message result)
        {
            BuildDateTimeAttribute buildAttribute = (BuildDateTimeAttribute)Attribute.GetCustomAttribute(System.Reflection.Assembly.GetExecutingAssembly(), typeof(BuildDateTimeAttribute))!;
            StringBuilder builder = new();
            builder.AppendLine("=== Duet Control Server ===");
            builder.AppendLine($"Duet Control Server version {Program.Version} ({buildAttribute.Date ?? "unknown build time"}, {(Environment.Is64BitProcess ? "64-bit" : "32-bit")})");

            Processor.Diagnostics(builder);
            await JobProcessor.Diagnostics(builder);
            IPC.Processors.CodeInterception.Diagnostics(builder);
            Provider.Diagnostics(builder);
            await SPI.Interface.Diagnostics(builder);

            result.Append(MessageType.Success, builder.ToString());
        }
    }
}
