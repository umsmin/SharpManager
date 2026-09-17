using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;

namespace SharpManager
{
    /// <summary>
    /// Class representing the floppy disk drive
    /// </summary>
    public class CE140F : NotifyObject
    {
        private enum NextCommand
        {
            None,
            BinarySave,
            TextSave,
            Print
        }

        private enum InputType
        {
            String,
            Number
        }

        private enum RecordFormat
        {
            Ascii,
            Binary
        }

        private sealed class FileHandle : IDisposable
        {
            public FileHandle(FileStream stream, RecordFormat recordFormat = RecordFormat.Ascii)
            {
                Stream = stream;
                RecordFormat = recordFormat;
            }

            public FileStream Stream { get; }

            public RecordFormat RecordFormat { get; }

            public int? LookAhead { get; set; }

            public int Size { get; set; }

            public int ReadByte()
            {
                var value = LookAhead ?? Stream.ReadByte();
                LookAhead = null;
                return value;
            }

            public void Dispose() => Stream.Dispose();
        }

        /// <summary>The maximum number of open files</summary>
        private const int MaxFileHandles = 6;

        /// <summary>
        /// Gets or sets of the directory representing the disk
        /// </summary>
        public string? DiskDirectory
        {
            get => directoryInfo?.FullName;
            set
            {
                directoryInfo = (!string.IsNullOrEmpty(value)) ? new DirectoryInfo(value) : null;
                if (!(directoryInfo?.Exists ?? false)) directoryInfo = null;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The directory information
        /// </summary>
        private DirectoryInfo? directoryInfo;

        /// <summary>
        /// The list of files in the directory
        /// </summary>
        private readonly List<string> files = new();

        /// <summary>The index of the current file in the directory listing</summary>
        private int fileIndex = 0;

        /// <summary>The message target</summary>
        private readonly IDebugTarget messageTarget;

        /// <summary>The currently open file</summary>
        private FileHandle? currentFile = null;

        /// <summary>The error frame</summary>
        private readonly byte[] ErrorFrame = new byte[] { 0xFF, 0 };

        /// <summary>The file handles</summary>
        private readonly FileHandle?[] fileHandles = new FileHandle[MaxFileHandles];

        /// <summary>Gets the next command to process</summary>
        private NextCommand nextCommand = NextCommand.None;

        /// <summary>
        /// Initializes a new instance of the <see cref="CE140F"/> class.
        /// </summary>
        /// <param name="messageTarget">The message log.</param>
        public CE140F(IDebugTarget messageTarget)
        {
            this.messageTarget = messageTarget;
        }

        /// <summary>
        /// Resets this instance.
        /// </summary>
        public void Reset()
        {
            files.Clear();
            fileIndex = 0;
            currentFile?.Dispose();
            currentFile = null;
            for (int i = 0; i < MaxFileHandles; i++)
            {
                fileHandles[i]?.Dispose();
                fileHandles[i] = null;
            }
        }

        /// <summary>
        /// Processes the command.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns>The response from the command</returns>
        public DiskResponse ProcessCommand(byte[] data)
        {
            return new DiskResponse(ProcessCommandInternal(data), nextCommand != NextCommand.None);
        }

        /// <summary>
        /// Processes the command main implementation
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns>The response from the command</returns>
        private byte[] ProcessCommandInternal(byte[] data)
        {
            // Save the current mode and reset to none berfore calling the mode commands
            if (nextCommand != NextCommand.None)
            {
                var currentMode = nextCommand;
                nextCommand = NextCommand.None;
                if (currentMode == NextCommand.TextSave) return CommandSaveWriteLine(data);
                if (currentMode == NextCommand.BinarySave) return CommandSaveWriteBinary(data);
                if (currentMode == NextCommand.Print) return CommandPrintWrite(data);
            }

            messageTarget.DebugWrite($"Disk Command #{data[0]:X2} ");

            switch (data[0])
            {
                case 0x05: return CommandFilesInit();
                case 0x06: return CommandFilesItem(false);
                case 0x07: return CommandFilesItem(true);
                case 0x0E: return CommandLoadOpen(data);
                case 0x17: return CommandLoadReadByte();
                case 0x12: return CommandLoadReadLine();
                case 0x0F: return CommandLoadBinary();
                case 0x1D: return CommandDiskFree(data);
                case 0x10: return CommandSaveOpen(data);
                case 0x11: return CommandSaveBinary(data);
                case 0x16: return CommandSaveText();
                case 0x03: return CommandOpen(data);
                case 0x04: return CommandClose(data);
                case 0x0A: return CommandKill(data);
                case 0x13: return CommandInput(data, InputType.String, false);  // String         INPUT #x, X$
                case 0x14: return CommandInput(data, InputType.Number, false);  // Number         INPUT #x, X
                case 0x15: return CommandPrint(data);
                case 0x1F: return CommandInput(data, InputType.String, true);   // String array   INPUT #x, X$(*)
                case 0x20: return CommandInput(data, InputType.Number, true);   // Number array   INPUT #x, X(*)

                /*
                //case 0x08: process_INIT(0x08);break;
                //case 0x09: process_INIT(0x09);break;
                //    case 0x0B: process_NAME(0x0B);break;
                //    case 0x0C: process_SET(0x0C);break;
                //    case 0x0D: process_COPY(0x0D);break;
                //    case 0x1A: process_EOF(0x1A);break;
                //    case 0x1C: process_LOC(0x1C);break;
                */
                default:
                    messageTarget.WriteLine($"Unknown disk command {data[0]:X2}");
                    return [0xFF, 0];
            }
        }

        /// <summary>
        /// Commands the disk free.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandDiskFree(byte[] data)
        {
            int driveNumber = data[1];
            int diskFree = 65000;
            messageTarget.WriteLine($"DSKF({driveNumber}) = {diskFree:N0}");
            var result = new List<byte>();
            result.AddSize(diskFree);
            return result.ToFrame();
        }

        /// <summary>
        /// The FILES command which parses the directory and returns the total number of files
        /// </summary>
        /// <returns>Number of files</returns>
        private byte[] CommandFilesInit()
        {
            messageTarget.WriteLine($"FILES");
            fileIndex = 0;
            files.Clear();
            if (directoryInfo != null) foreach (var file in directoryInfo.EnumerateFiles())
            {
                files.Add(file.Name);
            }
            return CreateFrame((byte)files.Count);
        }

        /// <summary>
        /// Returns the file name at the current index and then increment or decrements the index
        /// </summary>
        /// <param name="previous">if set to <c>true</c> then previous.</param>
        /// <returns>File name</returns>
        private byte[] CommandFilesItem(bool previous)
        {
            messageTarget.WriteLine($"FILES " + (previous ? "<Previous>" : "<Next>"));

            // If no files, return error to computer
            if (files.Count == 0) return ErrorFrame;

            // Add file name to the result
            var result = new List<byte>();
            result.AddString(FormatFileName(files[fileIndex]));

            // Update the index
            fileIndex += previous ? -1 : 1;
            if (fileIndex < 0) fileIndex = 0;
            if (fileIndex > files.Count - 1) fileIndex = files.Count - 1;

            return result.ToFrame();
        }

        /// <summary>
        /// Opens a file
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandLoadOpen(byte[] data)
        {
            string fileName = Encoding.ASCII.GetString(data, 3, 12).Replace(" ", "");
            messageTarget.WriteLine($"LOAD \"{fileName}\"");

            var result = new List<byte>();
            result.AddString(" ");

            if (directoryInfo == null)
            {
                messageTarget.WriteLine("No directory selected for drive.");
                result.AddSize(0);
                return result.ToFrame();
            }

            // Get filename
            string filePath = Path.Combine(directoryInfo.FullName, fileName);
            messageTarget.DebugWriteLine($"  {filePath}");
            currentFile = new FileHandle(File.OpenRead(filePath));
            result.AddSize((int)currentFile.Stream.Length);
            return result.ToFrame();
        }

        /// <summary>
        /// Commands the load read byte.
        /// </summary>
        /// <returns></returns>
        private byte[] CommandLoadReadByte()
        {
            var result = new List<byte>();
            var value = currentFile?.ReadByte() ?? -1;
            if (value == -1) return ErrorFrame;
            result.Add((byte)value);
            return result.ToFrame();
        }

        /// <summary>
        /// Commands the load read line.
        /// </summary>
        /// <returns></returns>
        private byte[] CommandLoadReadLine() 
        {
            messageTarget.DebugWriteLine("Load text file line");
            var result = new List<byte>();
            while (true)
            {
                var value = currentFile?.ReadByte() ?? -1;
                if (value == -1)
                {
                    result.Add(0x1A);           // Send EOF
                    currentFile?.Dispose();     // Close file
                    currentFile = null;
                    break;                  // End
                }
                if (IsNewLineChar(value))
                {
                    // Consume an optional LF after CR, preserving any other byte for the next read.
                    if (value == Ascii.CR && currentFile != null)
                    {
                        var nextValue = currentFile.ReadByte();
                        if (nextValue != -1 && nextValue != Ascii.LF) currentFile.LookAhead = nextValue;
                    }
                    result.Add(Ascii.CR);
                    break;
                }
                result.Add((byte)value);    // Send byte
            }
            result.AddFrame();              // Frame the line
            result.Add(0);                  // Add an additional zero byte
            return result.ToArray();
        }

        /// <summary>
        /// Commands the load binary.
        /// </summary>
        /// <returns></returns>
        private byte[] CommandLoadBinary()
        {
            messageTarget.DebugWriteLine("Load binary file data");
            var result = new List<byte>();
            result.Add(0);                      // Start of frame
            var buffer = new byte[256];
            while (true)
            {
                int bytesRead = currentFile?.Stream.Read(buffer, 0, buffer.Length) ?? 0;
                if (bytesRead == 0) break;
                result.AddBlock(new ArraySegment<byte>(buffer, 0, bytesRead));
            }
            result.Add(0);                      // Additional zero byte
            currentFile?.Dispose();             // Close file
            currentFile = null;
            return result.ToArray();
        }

        /// <summary>
        /// The save command
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandSaveOpen(byte[] data)
        {
            string fileName = Encoding.ASCII.GetString(data, 3, 12).Replace(" ", "");
            messageTarget.WriteLine($"SAVE \"{fileName}\"");

            if (directoryInfo == null)
            {
                messageTarget.WriteLine("No directory selected for drive.");
                return CreateResult(false);
            }

            // Get filename
            string filePath = Path.Combine(directoryInfo.FullName, fileName);
            messageTarget.DebugWriteLine($"  {filePath}");
            currentFile = new FileHandle(File.OpenWrite(filePath));
            return CreateResult(true);
        }

        /// <summary>
        /// Commands the save binary.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandSaveBinary(byte[] data)
        {
            if (currentFile == null) return CreateResult(false);

            currentFile.Size = data[2] + (data[3] << 8) + (data[4] << 16);
            messageTarget.DebugWriteLine($"Save binary file (size {currentFile.Size:n0})");
            nextCommand = NextCommand.BinarySave;
            return CreateResult(true);
        }

        /// <summary>
        /// Commands the save text.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandSaveText()
        {
            messageTarget.DebugWriteLine($"Save text file");
            nextCommand = NextCommand.TextSave;
            return CreateResult(true);
        }

        /// <summary>
        /// Save file text line
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandSaveWriteLine(byte[] data)
        {
            // File not open
            if (currentFile == null) return CreateResult(false);

            // End of file
            if (data[0] == 0x1A)
            {
                messageTarget.WriteLine("Done.");
                currentFile.Dispose();
                currentFile = null;
                return CreateResult(true);
            }

            // messageTarget.Write("."); TODO remove

            // Last byte is checksum so ignore
            for (int i = 0; i < data.Length - 1; i++)
            {
                currentFile.Stream.WriteByte(data[i]);
            }

            return CreateResult(true);
        }

        /// <summary>
        /// Save file binary
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandSaveWriteBinary(byte[] data)
        {
            // File not open
            if (currentFile == null) return CreateResult(false);

            // Write the data -- last byte is checksum
            for (int i = 0; i < data.Length - 1; i++)
            {
                // messageTarget.Write("."); TODO remove
                currentFile.Stream.WriteByte(data[i]);
            }

            // If the file is the correct size, close the file
            if (currentFile.Stream.Length == currentFile.Size)
            {
                messageTarget.WriteLine("Done.");
                currentFile.Dispose();
                currentFile = null;
            }
            else
            {
                // Otherwise receive the next block
                nextCommand = NextCommand.BinarySave;
            }

            return CreateResult(true);
        }

        /// <summary>
        /// Close a file or all files
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandClose(byte[] data)
        {
            int fileNumber = data[1];
            if (fileNumber == 0xFF)
            {
                messageTarget.WriteLine($"CLOSE <All>");
                for (int i = 0; i < MaxFileHandles; i++)
                {
                    fileHandles[i]?.Dispose();
                    fileHandles[i] = null;
                }
            }
            else
            {
                messageTarget.WriteLine($"CLOSE #{fileNumber:X2}");
                int fileIndex = fileNumber - 2;        // Convert to index
                if (!IsValidFileIndex(fileIndex))
                {
                    messageTarget.WriteLine($"Invalid file #{fileNumber}");
                    return CreateResult(false);
                }

                fileHandles[fileIndex]?.Dispose();
                fileHandles[fileIndex] = null;

            }

            return CreateResult(true);
        }

        /// <summary>
        /// Open a file
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandOpen(byte[] data)
        {
            if (directoryInfo == null)
            {
                messageTarget.WriteLine("No directory selected for drive.");
                return CreateResult(false);
            }

            // Get filename
            string fileName = Encoding.ASCII.GetString(data, 3, 12).Replace(" ", "");
            int fileMode = data[15];            // 1: input, 2: output, 3: append
            int fileNumber = data[16];          // file#
            int fileIndex = fileNumber - 2;     // file index
            string fileModeText = fileMode == 1 ? "INPUT" : (fileMode == 2 ? "OUTPUT" : "APPEND");

            messageTarget.WriteLine($"OPEN \"{fileName}\" FOR {fileModeText} AS #{fileNumber}");
            if (!IsValidFileIndex(fileIndex))
            {
                messageTarget.WriteLine($"Invalid file #{fileNumber}");
                return CreateResult(false);
            }

            if (fileHandles[fileIndex] != null)
            {
                fileHandles[fileIndex]?.Dispose();
                fileHandles[fileIndex] = null;
            }

            var recordFormat = GetRecordFormat(fileName);
            fileName = Path.Combine(directoryInfo.FullName, fileName);

            try
            {
                if (fileMode == 1) fileHandles[fileIndex] = new FileHandle(new FileStream(fileName, FileMode.Open, FileAccess.Read), recordFormat);
                else if (fileMode == 2) fileHandles[fileIndex] = new FileHandle(new FileStream(fileName, FileMode.Create, FileAccess.Write), recordFormat);
                else if (fileMode == 3) fileHandles[fileIndex] = new FileHandle(new FileStream(fileName, FileMode.Append, FileAccess.Write), recordFormat);
                else
                {
                    messageTarget.WriteLine($"Invalid file mode {fileMode}");
                    return CreateResult(false);
                }
            }
            catch (Exception ex)
            {
                messageTarget.WriteLine($"Error {ex.Message}");
                return CreateResult(false);
            }

            return CreateResult(true);
        }

        /// <summary>
        /// Print to an open file
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandPrint(byte[] data)
        {
            int fileNumber = data[1];
            int fileIndex = fileNumber - 2;
            messageTarget.WriteLine($"PRINT #{fileNumber}");
            if (!IsValidFileIndex(fileIndex))
            {
                messageTarget.WriteLine($"Invalid file #{fileNumber}");
                return CreateResult(false);
            }
            var file = fileHandles[fileIndex];
            if (file == null)
            {
                messageTarget.WriteLine($"File #{fileNumber} not open");
                return CreateResult(false);
            }
            if (!file.Stream.CanWrite)
            {
                messageTarget.WriteLine($"File #{fileNumber} not writable");
                return CreateResult(false);
            }

            nextCommand = NextCommand.Print;
            currentFile = file;
            return CreateResult(true);
        }

        /// <summary>
        /// Print line to file
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandPrintWrite(byte[] data)
        {
            // No current file
            if (currentFile == null) return CreateResult(false);

            if (currentFile.RecordFormat == RecordFormat.Binary) WriteBinaryRecord(currentFile.Stream, data);
            else WriteAsciiRecord(currentFile.Stream, data);

            return CreateResult(true);
        }

        /// <summary>
        /// Read line from file
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandInput(byte[] data, InputType type, bool isArray)
        {
            int fileNumber = data[1];
            int fileIndex = fileNumber - 2;
            messageTarget.WriteLine($"INPUT #{fileNumber} {type}");
            if (!IsValidFileIndex(fileIndex))
            {
                messageTarget.WriteLine($"Invalid file #{fileNumber}");
                return CreateResult(false);
            }
            var file = fileHandles[fileIndex];
            if (file == null)
            {
                messageTarget.WriteLine($"File #{fileNumber} not open");
                return CreateResult(false);
            }
            if (!file.Stream.CanRead)
            {
                messageTarget.WriteLine($"File #{fileNumber} not readable");
                return CreateResult(false);
            }

            if (file.RecordFormat == RecordFormat.Binary) return ReadBinaryInput(file.Stream, type, isArray);
            return ReadAsciiInput(file, type, isArray);
        }

        /// <summary>
        /// Write an ASCII record to the file.
        /// </summary>
        /// <param name="file">The file.</param>
        /// <param name="data">The data.</param>
        private static void WriteAsciiRecord(FileStream file, byte[] data)
        {
            // Write data
            for (int i = 0; i < data.Length - 2; i++)  // Ignore 0 and checksum
            {
                file.WriteByte(data[i]);
            }
        }

        /// <summary>
        /// Write a null-terminated record to the file.
        /// </summary>
        /// <param name="file">The file.</param>
        /// <param name="data">The data.</param>
        private static void WriteBinaryRecord(FileStream file, byte[] data)
        {
            int recordLength = Math.Max(0, data.Length - 2);  // Ignore 0 and checksum
            if (recordLength >= 2 && data[recordLength - 2] == Ascii.CR && data[recordLength - 1] == Ascii.LF)
            {
                recordLength -= 2;
            }

            for (int i = 0; i < recordLength; i++)
            {
                file.WriteByte(data[i]);
            }

            file.WriteByte(Ascii.NUL);
        }

        /// <summary>
        /// Read an ASCII record from the file.
        /// </summary>
        /// <param name="file">The file.</param>
        /// <param name="type">The input type.</param>
        /// <param name="isArray">if set to <c>true</c> then array.</param>
        /// <returns></returns>
        private static byte[] ReadAsciiInput(FileHandle file, InputType type, bool isArray)
        {
            // True if a numeric character was returned in the output
            bool isNumber = false;

            // Read line from the file
            var result = new List<byte>();

            while (true)
            {
                // Grab a value from the lookahead or the file directly
                var value = file.LookAhead ?? file.Stream.ReadByte();               
                // If at the end of the file, return
                if (value == -1) break;
                // If the type is number and not loading an array, split numbers that exist in a line
                if (type == InputType.Number && !isArray)
                {
                    // If a number exists, the value is not whitespace, and not already the lookahead, return the number
                    if (isNumber && !IsNumericCharacter(value) && !file.LookAhead.HasValue)
                    {
                        if (!IsWhitespace(value) && value != ',') file.LookAhead = value;
                        break;
                    }
                    isNumber = isNumber || IsNumber(value);
                }
                 
                file.LookAhead = null;
                if (isArray || !IsNewLineChar(value)) result.Add((byte)value);    // If not a newline char, send
                if (value == 0x0A && !isArray) break;                  // If LF then end line
            }
            result.Add(0);                  // Add an additional zero byte
            result.AddFrame();              // Frame the line
            result.Add(0);                  // Add an additional zero byte
            return result.ToArray();
        }

        /// <summary>
        /// Read a null-terminated record from the file.
        /// </summary>
        /// <param name="file">The file.</param>
        /// <param name="type">The input type.</param>
        /// <param name="isArray">if set to <c>true</c> then array.</param>
        /// <returns></returns>
        private static byte[] ReadBinaryInput(FileStream file, InputType type, bool isArray)
        {
            var record = new List<byte>();
            while (true)
            {
                var value = file.ReadByte();
                if (value == -1 || value == Ascii.NUL) break;
                record.Add((byte)value);
            }

            var result = type == InputType.Number && !isArray ? ReadFirstNumber(record) : record;
            result.Add(0);                  // Add an additional zero byte
            result.AddFrame();              // Frame the line
            result.Add(0);                  // Add an additional zero byte
            return result.ToArray();
        }

        /// <summary>
        /// Read the first complete number in a record.
        /// </summary>
        /// <param name="record">The record.</param>
        /// <returns></returns>
        private static List<byte> ReadFirstNumber(IEnumerable<byte> record)
        {
            var result = new List<byte>();
            bool hasStarted = false;

            foreach (var value in record)
            {
                if (!hasStarted && !IsNumericCharacter(value)) continue;
                if (hasStarted && !IsNumericCharacter(value)) break;

                result.Add(value);
                hasStarted = true;
            }

            return result;
        }

        /// <summary>
        /// Kill command deletes a file
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] CommandKill(byte[] data)
        {
            string fileName = Encoding.ASCII.GetString(data, 3, 12).Replace(" ", "");
            messageTarget.WriteLine($"KILL \"{fileName}\"");
            // TODO implement kill
            return CreateResult(false);
        }

        /// <summary>
        /// Formats the name of the file for the Sharp Pocket PC
        /// </summary>
        /// <param name="fileName">Name of the file.</param>
        /// <returns></returns>
        private static string FormatFileName(string fileName)
        {
            const string drivePrefix = "X:";
            const int maxNameLength = 8;
            const int maxExtensionLength = 3;

            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName).TrimStart('.'); // Remove the dot

            // Ensure the name and extension are within the limits
            name = name.Length > maxNameLength ? name[..maxNameLength] : name.PadRight(maxNameLength, ' ');
            extension = extension.Length > maxExtensionLength ? extension[..maxExtensionLength] : extension.PadRight(maxExtensionLength, ' ');
            return $"{drivePrefix}{name}.{extension} ";
        }

        /// <summary>
        /// Gets the record format for a file.
        /// </summary>
        /// <param name="fileName">Name of the file.</param>
        /// <returns></returns>
        private static RecordFormat GetRecordFormat(string fileName)
        {
            var extension = Path.GetExtension(fileName).TrimStart('.');
            return extension.Equals("DAT", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals("BIN", StringComparison.OrdinalIgnoreCase)
                ? RecordFormat.Binary
                : RecordFormat.Ascii;
        }

        /// <summary>
        /// Determines whether the file index is valid.
        /// </summary>
        /// <param name="fileIndex">Index of the file.</param>
        /// <returns></returns>
        private static bool IsValidFileIndex(int fileIndex) => fileIndex >= 0 && fileIndex < MaxFileHandles;

        /// <summary>
        /// Creates the frame.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private static byte[] CreateFrame(params byte[] data)
        {
            var result = new List<byte>();
            result.AddRange(data);
            return result.ToFrame();
        }

        /// <summary>
        /// Creates the array.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private static byte[] CreateArray(params byte[] data) => data;

        /// <summary>
        /// Creates the result.
        /// </summary>
        /// <param name="success">if set to <c>true</c> [success].</param>
        /// <returns></returns>
        private static byte[] CreateResult(bool success) => CreateArray(success ? (byte)0 : (byte)0xFF);

        private static bool IsNumericCharacter(int value) => value == '-' || value == '.' || IsNumber(value);

        private static bool IsWhitespace(int value) => value == ' ' || value == 0x0D || value == 0x0A || value == 0x09;

        private static bool IsNumber(int value) => (value >= '0' && value <= '9');

        private static bool IsNewLineChar(int value) => value == 0x0D || value == 0x0A;
    }
}
