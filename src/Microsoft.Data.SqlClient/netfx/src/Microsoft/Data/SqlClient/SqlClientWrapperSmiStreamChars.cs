// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.IO;
using Microsoft.Data.Common;
using Microsoft.Data.SqlTypes;

namespace Microsoft.Data.SqlClient.Server
{
    // Simple SqlStreamChars wrapper over SmiStream that handles server events on the 
    //  SqlClient side of Smi
    internal class SqlClientWrapperSmiStreamChars : SqlStreamChars
    {

        private SmiEventSink_Default _sink;
        private SmiStream _stream;

        internal SqlClientWrapperSmiStreamChars(SmiEventSink_Default sink, SmiStream stream)
        {
            Debug.Assert(sink != null);
            Debug.Assert(stream != null);
            _sink = sink;
            _stream = stream;
        }

        public override bool IsNull
        {
            get
            {
                return _stream == null;
            }
        }

        public override bool CanRead
        {
            get
            {
                return _stream.CanRead;
            }
        }

        // If CanSeek is false, Position, Seek, Length, and SetLength should throw.
        public override bool CanSeek
        {
            get
            {
                return _stream.CanSeek;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return _stream.CanWrite;
            }
        }

        public override long Length
        {
            get
            {
                long length = _stream.GetLength(_sink);
                _sink.ProcessMessagesAndThrow();
                if (length > 0)
                    return length / sizeof(char);
                else
                    return length;
            }
        }

        public override long Position
        {
            get
            {
                long position = _stream.GetPosition(_sink) / sizeof(char);
                _sink.ProcessMessagesAndThrow();
                return position;
            }
            set
            {
                if (value < 0)
                {
                    throw ADP.ArgumentOutOfRange("Position");
                }
                _stream.SetPosition(_sink, value * sizeof(char));
                _sink.ProcessMessagesAndThrow();
            }
        }

        public override void Flush()
        {
            _stream.Flush(_sink);
            _sink.ProcessMessagesAndThrow();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long result = _stream.Seek(_sink, offset * sizeof(char), origin);
            _sink.ProcessMessagesAndThrow();
            return result;
        }

        public override void SetLength(long value)
        {
            if (value < 0)
            {
                throw ADP.ArgumentOutOfRange("value");
            }
            _stream.SetLength(_sink, value * sizeof(char));
            _sink.ProcessMessagesAndThrow();
        }

        public override int Read(char[] buffer, int offset, int count)
        {
            int bytesRead = _stream.Read(_sink, buffer, offset * sizeof(char), count);
            _sink.ProcessMessagesAndThrow();
            return bytesRead;
        }

        public override void Write(char[] buffer, int offset, int count)
        {
            _stream.Write(_sink, buffer, offset, count);
            _sink.ProcessMessagesAndThrow();
        }

        // Convenience methods to allow simple pulling/pushing of raw bytes
        internal int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = _stream.Read(_sink, buffer, offset, count);
            _sink.ProcessMessagesAndThrow();
            return bytesRead;
        }

        internal void Write(byte[] buffer, int offset, int count)
        {
            _stream.Write(_sink, buffer, offset, count);
            _sink.ProcessMessagesAndThrow();
        }
    }

}



