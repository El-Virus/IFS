﻿using IFS.BSP;
using IFS.Logging;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO;

namespace IFS.FTP
{
    public enum FTPCommand
    {
        Invalid = 0,
        Retrieve = 1,
        Store = 2,
        NewStore = 9,
        Yes = 3,
        No = 4,
        HereIsPropertyList = 11,
        HereIsFile = 5,
        Version = 8,
        Comment = 7,
        EndOfCommand = 6,
        Enumerate = 10,
        NewEnumerate = 12,
        Delete = 14,
        Rename = 15,
    }

    public enum NoCode
    {
        UnimplmentedCommand = 1,
        UserNameRequired = 2,
        IllegalCommand = 3,

        MalformedPropertyList = 8,
        IllegalServerFilename = 9,
        IllegalDirectory = 10,
        IllegalNameBody = 11,
        IllegalVersion = 12,
        IllegalType = 13,
        IllegalByteSize = 14,
        IllegalEndOfLineConvention = 15,
        IllegalUserName = 16,
        IllegalUserPassword = 17,
        IllegalUserAccount = 18,
        IllegalConnectName = 19,
        IllegalConnectPassword = 20,
        IllegalCreationDate = 21,
        IllegalWriteDate = 22,
        IllegalReadDate = 23,
        IllegalAuthor = 24,
        IllegalDevice = 25,

        FileNotFound = 64,
        AccessDenied = 65,
        TransferParamsInvalid = 66,
        FileDataError = 67,
        FileTooLong = 68,
        DoNotSendFile = 69,
        StoreNotCompleted = 70,
        TransientServerFailure = 71,
        PermamentServerFailure = 72,
        FileBusy = 73,
        FileAlreadyExists = 74
    }

    struct FTPYesNoVersion
    {
        public FTPYesNoVersion(byte code, string message)
        {
            Code = code;
            Message = message;
        }

        public byte Code;
        public string Message;
    }    

    public class FTPServer : BSPProtocol
    {
        /// <summary>
        /// Called by dispatcher to send incoming data destined for this protocol.
        /// </summary>
        /// <param name="p"></param>
        public override void RecvData(PUP p)
        {
            throw new NotImplementedException();
        }

        public override void InitializeServerForChannel(BSPChannel channel)
        {
            // Spawn new worker
            FTPWorker ftpWorker = new FTPWorker(channel);
        }        
    }

    public class FTPWorker
    {
        public FTPWorker(BSPChannel channel)
        {
            // Register for channel events
            channel.OnDestroy += OnChannelDestroyed;

            _running = true;

            _workerThread = new Thread(new ParameterizedThreadStart(FTPWorkerThreadInit));
            _workerThread.Start(channel);
        }

        private void OnChannelDestroyed()
        {
            // Tell the thread to exit and give it a short period to do so...
            _running = false;

            Log.Write(LogType.Verbose, LogComponent.FTP, "Asking FTP worker thread to exit...");
            _workerThread.Join(1000);

            if (_workerThread.IsAlive)
            {
                Logging.Log.Write(LogType.Verbose, LogComponent.FTP, "FTP worker thread did not exit, terminating.");
                _workerThread.Abort();
            }
        }

        private void FTPWorkerThreadInit(object obj)
        {
            _channel = (BSPChannel)obj;
            //
            // Run the worker thread.
            // If anything goes wrong, log the exception and tear down the BSP connection.
            //
            try
            {
                FTPWorkerThread();
            }
            catch (Exception e)
            {
                if (!(e is ThreadAbortException))
                {
                    Log.Write(LogType.Error, LogComponent.FTP, "FTP worker thread terminated with exception '{0}'.", e.Message);
                    _channel.SendAbort("Server encountered an error.");
                }
            }
        }

        private void FTPWorkerThread()
        {           
            while (_running)
            {
                byte[] data = null;

                FTPCommand command = ReadNextCommandWithData(out data);
           
                //
                // At this point we should have the entire command, execute it.
                //
                switch(command)
                {
                    case FTPCommand.Version:
                        {
                            FTPYesNoVersion version = (FTPYesNoVersion)Serializer.Deserialize(data, typeof(FTPYesNoVersion));
                            Log.Write(LogType.Normal, LogComponent.FTP, "Client FTP version is {0}, herald is '{1}'.", version.Code, version.Message);

                            //
                            // Return our Version.
                            FTPYesNoVersion serverVersion = new FTPYesNoVersion(1, "LCM IFS FTP of 4 Feb 2016.");
                            SendFTPResponse(FTPCommand.Version, serverVersion);                            
                        }
                        break;

                    case FTPCommand.Enumerate:
                        {
                            // Argument to Enumerate is a property list (string).
                            //
                            string fileSpec = Helpers.ArrayToString(data);
                            Log.Write(LogType.Verbose, LogComponent.FTP, "File spec for enumeration is '{0}'.", fileSpec);

                            PropertyList pl = new PropertyList(fileSpec);

                            EnumerateFiles(pl);                                                        
                        }
                        break;

                    case FTPCommand.Retrieve:
                        {
                            // Argument to Retrieve is a property list (string).
                            //
                            string fileSpec = Helpers.ArrayToString(data);
                            Log.Write(LogType.Verbose, LogComponent.FTP, "File spec for retrieve is '{0}'.", fileSpec);

                            PropertyList pl = new PropertyList(fileSpec);

                            RetrieveFiles(pl);

                        }
                        break;

                    case FTPCommand.Store:
                        {
                            // Argument to Store is a property list (string).
                            //
                            string fileSpec = Helpers.ArrayToString(data);
                            Log.Write(LogType.Verbose, LogComponent.FTP, "File spec for store is '{0}'.", fileSpec);

                            PropertyList pl = new PropertyList(fileSpec);

                            StoreFile(pl, false /* old */);
                        }
                        break;

                    case FTPCommand.NewStore:
                        {
                            // Argument to New-Store is a property list (string).
                            //
                            string fileSpec = Helpers.ArrayToString(data);
                            Log.Write(LogType.Verbose, LogComponent.FTP, "File spec for new-store is '{0}'.", fileSpec);

                            PropertyList pl = new PropertyList(fileSpec);

                            StoreFile(pl, true /* new */);
                        }
                        break;

                    case FTPCommand.Delete:
                        {
                            // Argument to New-Store is a property list (string).
                            //
                            string fileSpec = Helpers.ArrayToString(data);
                            Log.Write(LogType.Verbose, LogComponent.FTP, "File spec for new-store is '{0}'.", fileSpec);

                            PropertyList pl = new PropertyList(fileSpec);

                            DeleteFiles(pl);
                        }
                        break;

                    default:
                        Log.Write(LogType.Warning, LogComponent.FTP, "Unhandled FTP command {0}.", command);
                        break;
                }
            }                 
        }

        private FTPCommand ReadNextCommandWithData(out byte[] data)
        {
            // Discard input until we get a Mark.  We should (in general) get a
            // command, followed by EndOfCommand.  
            FTPCommand command = (FTPCommand)_channel.WaitForMark();

            data = ReadNextCommandData();            

            return command;
        }


        /// <summary>
        /// As above, but expects the channel read position is such that a Mark has *just* been read.        
        /// </summary>
        /// <returns></returns>
        private byte[] ReadNextCommandData()
        {
            // Read data until the next Mark, which should be "EndOfCommand"
            // TODO: I don't anticipate that any FTP command will contain more than 1k of data, this may need adjustment.
            byte[] data = null;
            FTPCommand lastMark = ReadUntilNextMark(out data, 1024);

            //
            // Ensure that next Mark is "EndOfCommand"
            //
            if (lastMark != FTPCommand.EndOfCommand)
            {
                throw new InvalidOperationException(String.Format("Expected EndOfCommand, got {0}", lastMark));
            }

            return data;         
        }

        /// <summary>
        /// Reads data from the channel into data until the next Mark is hit, this Mark is returned.        
        /// If the size of the read data exceeds maxSize an exception will be thrown.
        /// </summary>
        /// <param name="data">The data read from the channel.</param>
        /// <param name="maxSize">The maximum size (in bytes) of the data to read.</param>
        /// <returns>The next Mark encountered</returns>
        private FTPCommand ReadUntilNextMark(out byte[] data, int maxSize)
        {
            MemoryStream ms = new MemoryStream(16384);
            byte[] buffer = new byte[512];

            while(true)
            {
                int length = _channel.Read(ref buffer, buffer.Length);

                ms.Write(buffer, 0, length);

                if (ms.Length > maxSize)
                {
                    throw new InvalidOperationException("Data size limit exceeded.");
                }

                //
                // On a short read, we are done.
                //
                if (length < buffer.Length)
                {
                    break;
                }
            }

            data = ms.ToArray();
            return (FTPCommand)_channel.LastMark;
        }

        /// <summary>
        /// Enumerates the files matching the requested file specification.
        /// </summary>
        /// <param name="fileSpec"></param>
        private void EnumerateFiles(PropertyList fileSpec)
        {
            string fullPath = BuildAndValidateFilePath(fileSpec);

            if (fullPath == null)
            {
                return;
            }

            List<PropertyList> files = EnumerateFiles(fullPath);

            // Send each property list to the user
            foreach(PropertyList matchingFile in files)
            {                
                _channel.SendMark((byte)FTPCommand.HereIsPropertyList, false);
                _channel.Send(Helpers.StringToArray(matchingFile.ToString()));                
            }

            // End the enumeration.
            _channel.SendMark((byte)FTPCommand.EndOfCommand, true);

        }

        /// <summary>
        /// Enumerates the files matching the requested file specification and sends them to the client, one at a time.
        /// </summary>
        /// <param name="fileSpec"></param>
        private void RetrieveFiles(PropertyList fileSpec)
        {
            string fullPath = BuildAndValidateFilePath(fileSpec);

            if (fullPath == null)
            {
                return;
            }

            List<PropertyList> files = EnumerateFiles(fullPath);

            // Send each list to the user, followed by the actual file data.
            //
            foreach (PropertyList matchingFile in files)
            {
                Log.Write(LogType.Verbose, LogComponent.FTP, "Property list for file being sent is '{0}'", matchingFile.ToString());
                // Tell the client about the file we're about to send
                SendFTPResponse(FTPCommand.HereIsPropertyList, matchingFile);

                // Await confirmation:                
                byte[] data = null;
                FTPCommand yesNo = ReadNextCommandWithData(out data);

                if (yesNo == FTPCommand.No)
                {
                    // Skip this file
                    Log.Write(LogType.Verbose, LogComponent.FTP, "File skipped.");
                    continue;
                }

                using (FileStream outFile = OpenFile(matchingFile, true))
                {
                    Log.Write(LogType.Verbose, LogComponent.FTP, "Sending file...");

                    // Send the file data.
                    _channel.SendMark((byte)FTPCommand.HereIsFile, true);
                    data = new byte[512];

                    while (true)
                    {
                        int read = outFile.Read(data, 0, data.Length);

                        if (read == 0)
                        {
                            // Nothing to send, we're done.
                            break;
                        }

                        Log.Write(LogType.Verbose, LogComponent.FTP, "Sending data, current file position {0}.", outFile.Position);
                        _channel.Send(data, read, true);

                        if (read < data.Length)
                        {
                            // Short read, end of file.    
                            break;
                        }
                    }
                }

                // End the file successfully.  Note that we do NOT send an EOC here.
                Log.Write(LogType.Verbose, LogComponent.FTP, "Sent.");
                _channel.SendMark((byte)FTPCommand.Yes, false);
                _channel.Send(Serializer.Serialize(new FTPYesNoVersion(0, "File transferred successfully.")));
            }

            // End the transfer.
            Log.Write(LogType.Verbose, LogComponent.FTP, "All requested files sent.");
            _channel.SendMark((byte)FTPCommand.EndOfCommand, true);
        }


        private void StoreFile(PropertyList fileSpec, bool newStore)
        {
            string fullPath = BuildAndValidateFilePath(fileSpec);

            if (fullPath == null)
            {
                return;
            }

            if (newStore)
            {
                //
                // Send the client a Here-Is-Property-List for the file to be stored, indicating that we've accepted the request
                // for the store.
                // (If we're unable to write or fail for any reason, we can send a No later).
                //
                PropertyList fileProps = new PropertyList();

                fileProps.SetPropertyValue(KnownPropertyNames.ServerFilename, fullPath);                
                fileProps.SetPropertyValue(KnownPropertyNames.Type, "Binary");                  // We treat all files as binary for now
                fileProps.SetPropertyValue(KnownPropertyNames.ByteSize, "8");                   // 8-bit bytes, please.
                fileProps.SetPropertyValue(KnownPropertyNames.Version, "1");                    // No real versioning support

                SendFTPResponse(FTPCommand.HereIsPropertyList, fileProps);                                
            }

            //
            // We now expect a "Here-Is-File"...
            //
            FTPCommand hereIsFile = (FTPCommand)_channel.WaitForMark();

            if (hereIsFile != FTPCommand.HereIsFile)
            {
                throw new InvalidOperationException("Expected Here-Is-File from client.");
            }

            //
            // At this point the client should start sending data, so we should start receiving it.            
            // 
            string fullFileName = Path.Combine(Configuration.FTPRoot, fullPath);
            bool success = true;
            FTPCommand lastMark;
            byte[] buffer;            

            try
            {
                Log.Write(LogType.Verbose, LogComponent.FTP, "Receiving file {0}.", fullFileName);
                using (FileStream inFile = new FileStream(fullFileName, FileMode.Create, FileAccess.Write))
                {
                    // TODO: move to constant. Possibly make max size configurable.
                    // For now, it seems very unlikely that any Alto is going to have a single file larger than 4mb.
                    lastMark = ReadUntilNextMark(out buffer, 4096 * 1024);   

                    // Write out to file
                    inFile.Write(buffer, 0, buffer.Length);

                    Log.Write(LogType.Verbose, LogComponent.FTP, "Wrote {0} bytes to {1}.  Receive completed.", buffer.Length, fullFileName);
                }
            }
            catch (Exception e)
            {
                // We failed while writing the file, send a No response to the client.
                // Per the spec, we need to drain the client data first.
                lastMark = ReadUntilNextMark(out buffer, 4096 * 1024);   // TODO: move to constant
                success = false;

                Log.Write(LogType.Warning, LogComponent.FTP, "Failed to write {0}.  Error '{1}'", fullFileName, e.Message);
            }

            // Read in the last command we got (should be a Yes or No).  This is sort of annoying in that it breaks the normal convention of
            // Command followed by EndOfCommand, so we have to read the remainder of the Yes/No command separately.
            if (lastMark != FTPCommand.Yes && lastMark != FTPCommand.No)
            {
                throw new InvalidOperationException("Expected Yes or No response from client after transfer.");
            }

            buffer = ReadNextCommandData();
            FTPYesNoVersion clientYesNo = (FTPYesNoVersion)Serializer.Deserialize(buffer, typeof(FTPYesNoVersion));

            Log.Write(LogType.Verbose, LogComponent.FTP, "Client success code is {0}, {1}, '{2}'", lastMark, clientYesNo.Code, clientYesNo.Code);

            if (!success)
            {
                // TODO: provide actual No codes.
                SendFTPNoResponse(NoCode.FileBusy, "File transfer failed.");
            }
            else
            {
                SendFTPYesResponse("File transfer completed.");
            }

            // If we failed to write a complete file, try to see that it gets cleaned up.
            if (!success || lastMark == FTPCommand.No)
            {
                try
                {
                    File.Delete(Path.Combine(Configuration.FTPRoot, fullPath));
                }
                catch
                {
                    // Just eat the exception, we tried our best...
                }
            }

            Log.Write(LogType.Verbose, LogComponent.FTP, "Transfer done.");
        }

        /// <summary>
        /// Deletes the files matching the requested file specification and sends them to the client, one at a time.
        /// </summary>
        /// <param name="fileSpec"></param>
        private void DeleteFiles(PropertyList fileSpec)
        {
            string fullPath = BuildAndValidateFilePath(fileSpec);

            if (fullPath == null)
            {
                return;
            }

            List<PropertyList> files = EnumerateFiles(fullPath);

            // Send each list to the user, followed by the actual file data.
            //
            foreach (PropertyList matchingFile in files)
            {
                Log.Write(LogType.Verbose, LogComponent.FTP, "Property list for file being sent is '{0}'", matchingFile.ToString());
                // Tell the client about the file we're about to send
                SendFTPResponse(FTPCommand.HereIsPropertyList, matchingFile);

                // Await confirmation:                
                byte[] data = null;
                FTPCommand yesNo = ReadNextCommandWithData(out data);

                if (yesNo == FTPCommand.No)
                {
                    // Skip this file
                    Log.Write(LogType.Verbose, LogComponent.FTP, "File skipped.");
                    continue;
                }

                // Go ahead and delete the file.
                //
                try
                {
                    File.Delete(
                        Path.Combine(
                            Configuration.FTPRoot, matchingFile.GetPropertyValue(KnownPropertyNames.Directory), matchingFile.GetPropertyValue(KnownPropertyNames.ServerFilename)));

                    // End the file successfully.  Note that we do NOT send an EOC here, only after all files have been deleted.
                    Log.Write(LogType.Verbose, LogComponent.FTP, "Deleted.");
                    _channel.SendMark((byte)FTPCommand.Yes, false);
                    _channel.Send(Serializer.Serialize(new FTPYesNoVersion(0, "File deleted successfully.")));
                }
                catch(Exception e)
                {
                    // TODO: calculate real NO codes
                    _channel.SendMark((byte)FTPCommand.No, false);
                    _channel.Send(Serializer.Serialize(new FTPYesNoVersion((byte)NoCode.AccessDenied, e.Message)));
                }               
            }

            // End the transfer.
            Log.Write(LogType.Verbose, LogComponent.FTP, "All requested files deleted.");
            _channel.SendMark((byte)FTPCommand.EndOfCommand, true);
        }

        /// <summary>
        /// Open the file specified by the provided PropertyList
        /// </summary>
        /// <param name="fileSpec"></param>
        /// <returns></returns>
        private FileStream OpenFile(PropertyList fileSpec, bool readOnly)
        {
            string absolutePath = Path.Combine(Configuration.FTPRoot, fileSpec.GetPropertyValue(KnownPropertyNames.Directory), fileSpec.GetPropertyValue(KnownPropertyNames.ServerFilename));

            return new FileStream(absolutePath, FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite);
        }

        /// <summary>
        /// Enumerates all files in the IFS FTP directory matching the specified specification, and returns a full PropertyList for each.
        /// </summary>
        /// <param name="fileSpec"></param>
        /// <returns></returns>
        private List<PropertyList> EnumerateFiles(string fileSpec)
        {
            List<PropertyList> properties = new List<PropertyList>();            

            // Build a path rooted in the FTP root.
            string fullFileSpec = Path.Combine(Configuration.FTPRoot, fileSpec);

            // Split full path into filename and path parts
            string fileName = Path.GetFileName(fullFileSpec);
            string path = Path.GetDirectoryName(fullFileSpec);

            // Find all files that match the fileName (which may be a pattern to match or a complete file name for a single file)
            // These will be absolute paths.
            string[] matchingFiles = Directory.GetFiles(path, fileName, SearchOption.TopDirectoryOnly);

            // Build a property list containing the required properties.
            // For now, we ignore any Desired-Property requests (this is legal) and return all properties we know about.
            foreach (string matchingFile in matchingFiles)
            {
                string nameOnly = Path.GetFileName(matchingFile);

                PropertyList fileProps = new PropertyList();

                fileProps.SetPropertyValue(KnownPropertyNames.ServerFilename, nameOnly);
                fileProps.SetPropertyValue(KnownPropertyNames.Directory, path);
                fileProps.SetPropertyValue(KnownPropertyNames.NameBody, nameOnly);
                fileProps.SetPropertyValue(KnownPropertyNames.Type, "Binary");                  // We treat all files as binary for now
                fileProps.SetPropertyValue(KnownPropertyNames.ByteSize, "8");                   // 8-bit bytes, please.
                fileProps.SetPropertyValue(KnownPropertyNames.Version, "1");                    // No real versioning support
                fileProps.SetPropertyValue(KnownPropertyNames.CreationDate, File.GetCreationTime(matchingFile).ToString("dd-MMM-yy HH:mm:ss"));
                fileProps.SetPropertyValue(KnownPropertyNames.WriteDate, File.GetLastWriteTime(matchingFile).ToString("dd-MMM-yy HH:mm:ss"));
                fileProps.SetPropertyValue(KnownPropertyNames.ReadDate, File.GetLastAccessTime(matchingFile).ToString("dd-MMM-yy HH:mm:ss"));

                properties.Add(fileProps);
            }

            return properties;
        }

        /// <summary>
        /// Builds a relative path from the specified file PropertyList and checks for basic validity:
        ///  - that the syntax is correct
        ///  - that it includes no invalid characters. 
        ///  - that the directory specified actually exists.
        /// </summary>
        /// <param name="fileSpec"></param>
        /// <returns></returns>
        private string BuildAndValidateFilePath(PropertyList fileSpec)
        {
            //
            // Pull the file identifying properties from fileSpec and see what we can make of them.            
            //
            string serverFilename = fileSpec.GetPropertyValue(KnownPropertyNames.ServerFilename);
            string directory = fileSpec.GetPropertyValue(KnownPropertyNames.Directory);
            string nameBody = fileSpec.GetPropertyValue(KnownPropertyNames.NameBody);
            string version = fileSpec.GetPropertyValue(KnownPropertyNames.Version);

            // Sanity checks:

            // At the very least, one of Server-Filename or Name-Body must be specified.
            if (serverFilename == null && nameBody == null)
            {
                SendFTPNoResponse(NoCode.IllegalServerFilename, "Need at least a Server-FileName or Name-Body property.");
                return null;
            }

            //
            // Attempt to build a full file path from the bits we have.
            //
            string relativePath;

            if (directory != null && serverFilename != null)
            {
                //
                // If Directory and Server-Filename are both specified
                // We will assume Directory specifies the containing directory for Server-Filename, and prepend it.
                //
                relativePath = Path.Combine(directory, serverFilename);
            }
            else if (serverFilename != null)
            {
                // We will just use the Server-Filename as the complete path
                relativePath = serverFilename;
            }
            else
            {
                //
                // Directory was specified, Server-Filename was not, so we expect at least
                // Name-Body to be specified.
                if (nameBody == null)
                {
                    SendFTPNoResponse(NoCode.IllegalNameBody, "Name-Body must be specified.");
                    return null;
                }


                relativePath = Path.Combine(directory, nameBody);

                if (version != null)
                {
                    relativePath += ("!" + version);
                }
            }

            //
            // At this point we should have a path built.
            // Now let's see if it's valid.
            //

            //
            // Path should be relative:
            //
            if (Path.IsPathRooted(relativePath))
            {
                SendFTPNoResponse(NoCode.IllegalDirectory, "Path must be relative.");
                return null;
            }            

            // Build path combined with FTP root directory
            //
            string absolutePath = Path.Combine(Configuration.FTPRoot, relativePath);
            string absoluteDirectory = Path.GetDirectoryName(absolutePath);

            //
            // Path (including filename) must not contain any trickery like "..\" to try and escape from the directory root
            // And directory must not include invalid characters.  
            //
            if (relativePath.Contains("..\\") ||
                absoluteDirectory.IndexOfAny(Path.GetInvalidPathChars()) != -1)
            {
                SendFTPNoResponse(NoCode.IllegalDirectory, "Path is invalid.");
                return null;
            }

            //
            // Path must exist:
            //
            if (!Directory.Exists(absoluteDirectory))
            {
                SendFTPNoResponse(NoCode.FileNotFound, "Path does not exist.");
                return null;
            }        

            //
            // Looks like we should be OK.
            return relativePath;
        }

        private void SendFTPResponse(FTPCommand responseCommand, object data)
        {
            _channel.SendMark((byte)responseCommand, false);
            _channel.Send(Serializer.Serialize(data));
            _channel.SendMark((byte)FTPCommand.EndOfCommand, true);
        }

        private void SendFTPResponse(FTPCommand responseCommand, PropertyList data)
        {
            _channel.SendMark((byte)responseCommand, false);
            _channel.Send(Helpers.StringToArray(data.ToString()));
            _channel.SendMark((byte)FTPCommand.EndOfCommand, true);
        }

        private void SendFTPNoResponse(NoCode code, string message)
        {
            _channel.SendMark((byte)FTPCommand.No, false);
            _channel.Send(Serializer.Serialize(new FTPYesNoVersion((byte)code, message)));
            _channel.SendMark((byte)FTPCommand.EndOfCommand, true);
        }

        private void SendFTPYesResponse(string message)
        {
            _channel.SendMark((byte)FTPCommand.Yes, false);
            _channel.Send(Serializer.Serialize(new FTPYesNoVersion(1, message)));
            _channel.SendMark((byte)FTPCommand.EndOfCommand, true);
        }

        private BSPChannel _channel;

        private Thread _workerThread;
        private bool _running;
    }
}
