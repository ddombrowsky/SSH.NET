﻿#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

using Renci.SshNet.Abstractions;
using Renci.SshNet.Channels;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Connection;
using Renci.SshNet.Messages.Transport;

namespace Renci.SshNet
{
    /// <summary>
    /// Represents SSH command that can be executed.
    /// </summary>
    public class SshCommand : IDisposable
    {
        private static readonly object CompletedResult = new();

        private readonly ISession _session;
        private readonly Encoding _encoding;

        /// <summary>
        /// The result of the command: an exception, <see cref="CompletedResult"/>
        /// or <see langword="null"/>.
        /// </summary>
        private object? _result;

        private IChannelSession? _channel;
        private CommandAsyncResult? _asyncResult;
        private AsyncCallback? _callback;
        private string? _stdOut;
        private string? _stdErr;
        private bool _hasError;
        private bool _isDisposed;
        private ChannelInputStream? _inputStream;
        private TimeSpan _commandTimeout;

        private int _exitStatus;
        private volatile bool _haveExitStatus; // volatile to prevent re-ordering of reads/writes of _exitStatus.

        /// <summary>
        /// Gets the command text.
        /// </summary>
        public string CommandText { get; private set; }

        /// <summary>
        /// Gets or sets the command timeout.
        /// </summary>
        /// <value>
        /// The command timeout.
        /// </value>
        public TimeSpan CommandTimeout
        {
            get
            {
                return _commandTimeout;
            }
            set
            {
                value.EnsureValidTimeout(nameof(CommandTimeout));

                _commandTimeout = value;
            }
        }

        /// <summary>
        /// Gets the number representing the exit status of the command, if applicable,
        /// otherwise <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// The value is not <see langword="null"/> when an exit status code has been returned
        /// from the server. If the command terminated due to a signal, <see cref="ExitSignal"/>
        /// may be not <see langword="null"/> instead.
        /// </remarks>
        /// <seealso cref="ExitSignal"/>
        public int? ExitStatus
        {
            get
            {
                return _haveExitStatus ? _exitStatus : null;
            }
        }

        /// <summary>
        /// Gets the name of the signal due to which the command
        /// terminated violently, if applicable, otherwise <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// The value (if it exists) is supplied by the server and is usually one of the
        /// following, as described in https://datatracker.ietf.org/doc/html/rfc4254#section-6.10:
        /// ABRT, ALRM, FPE, HUP, ILL, INT, KILL, PIPE, QUIT, SEGV, TER, USR1, USR2.
        /// </remarks>
        public string? ExitSignal { get; private set; }

        /// <summary>
        /// Gets the output stream.
        /// </summary>
        public Stream OutputStream { get; private set; }

        /// <summary>
        /// Gets the extended output stream.
        /// </summary>
        public Stream ExtendedOutputStream { get; private set; }

        /// <summary>
        /// Creates and returns the input stream for the command.
        /// </summary>
        /// <returns>
        /// The stream that can be used to transfer data to the command's input stream.
        /// </returns>
        public Stream CreateInputStream()
        {
            if (_channel == null)
            {
                throw new InvalidOperationException($"The input stream can be used only after calling BeginExecute and before calling EndExecute.");
            }

            if (_inputStream != null)
            {
                throw new InvalidOperationException($"The input stream already exists.");
            }

            _inputStream = new ChannelInputStream(_channel);
            return _inputStream;
        }

        /// <summary>
        /// Gets the command execution result.
        /// </summary>
        public string Result
        {
            get
            {
                if (_stdOut is not null)
                {
                    return _stdOut;
                }

                if (_asyncResult is null)
                {
                    return string.Empty;
                }

                using (var sr = new StreamReader(OutputStream, _encoding))
                {
                    return _stdOut = sr.ReadToEnd();
                }
            }
        }

        /// <summary>
        /// Gets the command execution error.
        /// </summary>
        public string Error
        {
            get
            {
                if (_stdErr is not null)
                {
                    return _stdErr;
                }

                if (_asyncResult is null || !_hasError)
                {
                    return string.Empty;
                }

                using (var sr = new StreamReader(ExtendedOutputStream, _encoding))
                {
                    return _stdErr = sr.ReadToEnd();
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SshCommand"/> class.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="commandText">The command text.</param>
        /// <param name="encoding">The encoding to use for the results.</param>
        /// <exception cref="ArgumentNullException">Either <paramref name="session"/>, <paramref name="commandText"/> is <see langword="null"/>.</exception>
        internal SshCommand(ISession session, string commandText, Encoding encoding)
        {
            if (session is null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (commandText is null)
            {
                throw new ArgumentNullException(nameof(commandText));
            }

            if (encoding is null)
            {
                throw new ArgumentNullException(nameof(encoding));
            }

            _session = session;
            CommandText = commandText;
            _encoding = encoding;
            CommandTimeout = Timeout.InfiniteTimeSpan;
            OutputStream = new PipeStream();
            ExtendedOutputStream = new PipeStream();
            _session.Disconnected += Session_Disconnected;
            _session.ErrorOccured += Session_ErrorOccured;
        }

        /// <summary>
        /// Begins an asynchronous command execution.
        /// </summary>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that represents the asynchronous command execution, which could still be pending.
        /// </returns>
        /// <exception cref="InvalidOperationException">Asynchronous operation is already in progress.</exception>
        /// <exception cref="SshException">Invalid operation.</exception>
        /// <exception cref="ArgumentException">CommandText property is empty.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SshOperationTimeoutException">Operation has timed out.</exception>
        public IAsyncResult BeginExecute()
        {
            return BeginExecute(callback: null, state: null);
        }

        /// <summary>
        /// Begins an asynchronous command execution.
        /// </summary>
        /// <param name="callback">An optional asynchronous callback, to be called when the command execution is complete.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that represents the asynchronous command execution, which could still be pending.
        /// </returns>
        /// <exception cref="InvalidOperationException">Asynchronous operation is already in progress.</exception>
        /// <exception cref="SshException">Invalid operation.</exception>
        /// <exception cref="ArgumentException">CommandText property is empty.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SshOperationTimeoutException">Operation has timed out.</exception>
        public IAsyncResult BeginExecute(AsyncCallback? callback)
        {
            return BeginExecute(callback, state: null);
        }

        /// <summary>
        /// Begins an asynchronous command execution.
        /// </summary>
        /// <param name="callback">An optional asynchronous callback, to be called when the command execution is complete.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous read request from other requests.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that represents the asynchronous command execution, which could still be pending.
        /// </returns>
        /// <exception cref="InvalidOperationException">Asynchronous operation is already in progress.</exception>
        /// <exception cref="SshException">Invalid operation.</exception>
        /// <exception cref="ArgumentException">CommandText property is empty.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SshOperationTimeoutException">Operation has timed out.</exception>
        public IAsyncResult BeginExecute(AsyncCallback? callback, object? state)
        {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_isDisposed, this);
#else
            if (_isDisposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
#endif

            if (_asyncResult is not null)
            {
                if (!_asyncResult.AsyncWaitHandle.WaitOne(0))
                {
                    throw new InvalidOperationException("Asynchronous operation is already in progress.");
                }

                OutputStream.Dispose();
                ExtendedOutputStream.Dispose();

                // Initialize output streams. We already initialised them for the first
                // execution in the constructor (to allow passing them around before execution)
                // so we just need to reinitialise them for subsequent executions.
                OutputStream = new PipeStream();
                ExtendedOutputStream = new PipeStream();
            }

            // Create new AsyncResult object
            _asyncResult = new CommandAsyncResult
            {
                AsyncWaitHandle = new ManualResetEvent(initialState: false),
                AsyncState = state,
            };

            _exitStatus = default;
            _haveExitStatus = false;
            ExitSignal = null;
            _result = null;
            _stdOut = null;
            _stdErr = null;
            _hasError = false;
            _callback = callback;

            _channel = _session.CreateChannelSession();
            _channel.DataReceived += Channel_DataReceived;
            _channel.ExtendedDataReceived += Channel_ExtendedDataReceived;
            _channel.RequestReceived += Channel_RequestReceived;
            _channel.Closed += Channel_Closed;
            _channel.Open();

            _ = _channel.SendExecRequest(CommandText);

            return _asyncResult;
        }

        /// <summary>
        /// Begins an asynchronous command execution.
        /// </summary>
        /// <param name="commandText">The command text.</param>
        /// <param name="callback">An optional asynchronous callback, to be called when the command execution is complete.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous read request from other requests.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that represents the asynchronous command execution, which could still be pending.
        /// </returns>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SshOperationTimeoutException">Operation has timed out.</exception>
        public IAsyncResult BeginExecute(string commandText, AsyncCallback? callback, object? state)
        {
            if (commandText is null)
            {
                throw new ArgumentNullException(nameof(commandText));
            }

            CommandText = commandText;

            return BeginExecute(callback, state);
        }

        /// <summary>
        /// Waits for the pending asynchronous command execution to complete.
        /// </summary>
        /// <param name="asyncResult">The reference to the pending asynchronous request to finish.</param>
        /// <returns>Command execution result.</returns>
        /// <exception cref="ArgumentException">Either the IAsyncResult object did not come from the corresponding async method on this type, or EndExecute was called multiple times with the same IAsyncResult.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="asyncResult"/> is <see langword="null"/>.</exception>
        public string EndExecute(IAsyncResult asyncResult)
        {
            if (asyncResult is null)
            {
                throw new ArgumentNullException(nameof(asyncResult));
            }

            if (_asyncResult != asyncResult)
            {
                throw new ArgumentException("Argument does not correspond to the currently executing command.", nameof(asyncResult));
            }

            _inputStream?.Dispose();

            if (!_asyncResult.AsyncWaitHandle.WaitOne(CommandTimeout))
            {
                // Complete the operation with a TimeoutException (which will be thrown below).
                SetAsyncComplete(new SshOperationTimeoutException($"Command '{CommandText}' timed out. ({nameof(CommandTimeout)}: {CommandTimeout})."));
            }

            Debug.Assert(_asyncResult.IsCompleted);

            if (_result is Exception exception)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            Debug.Assert(_result == CompletedResult);
            Debug.Assert(!OutputStream.CanWrite, $"{nameof(OutputStream)} should have been disposed (else we will block).");

            return Result;
        }

        /// <summary>
        /// Cancels a running command by sending a signal to the remote process.
        /// </summary>
        /// <param name="forceKill">if true send SIGKILL instead of SIGTERM.</param>
        /// <param name="millisecondsTimeout">Time to wait for the server to reply.</param>
        /// <remarks>
        /// <para>
        /// This method stops the command running on the server by sending a SIGTERM
        /// (or SIGKILL, depending on <paramref name="forceKill"/>) signal to the remote
        /// process. When the server implements signals, it will send a response which
        /// populates <see cref="ExitSignal"/> with the signal with which the command terminated.
        /// </para>
        /// <para>
        /// When the server does not implement signals, it may send no response. As a fallback,
        /// this method waits up to <paramref name="millisecondsTimeout"/> for a response
        /// and then completes the <see cref="SshCommand"/> object anyway if there was none.
        /// </para>
        /// <para>
        /// If the command has already finished (with or without cancellation), this method does
        /// nothing.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">Command has not been started.</exception>
        public void CancelAsync(bool forceKill = false, int millisecondsTimeout = 500)
        {
            if (_asyncResult is not { } asyncResult)
            {
                throw new InvalidOperationException("Command has not been started.");
            }

            var exception = new OperationCanceledException($"Command '{CommandText}' was cancelled.");

            if (Interlocked.CompareExchange(ref _result, exception, comparand: null) is not null)
            {
                // Command has already completed.
                return;
            }

            // Try to send the cancellation signal.
            if (_channel?.SendSignalRequest(forceKill ? "KILL" : "TERM") is null)
            {
                // Command has completed (in the meantime since the last check).
                // We won the race above and the command has finished by some other means,
                // but will throw the OperationCanceledException.
                return;
            }

            // Having sent the "signal" message, we expect to receive "exit-signal"
            // and then a close message. But since a server may not implement signals,
            // we can't guarantee that, so we wait a short time for that to happen and
            // if it doesn't, just set the WaitHandle ourselves to unblock EndExecute.

            if (!asyncResult.AsyncWaitHandle.WaitOne(millisecondsTimeout))
            {
                SetAsyncComplete(asyncResult);
            }
        }

        /// <summary>
        /// Executes command specified by <see cref="CommandText"/> property.
        /// </summary>
        /// <returns>
        /// Command execution result.
        /// </returns>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SshOperationTimeoutException">Operation has timed out.</exception>
        public string Execute()
        {
            return EndExecute(BeginExecute(callback: null, state: null));
        }

        /// <summary>
        /// Executes the specified command text.
        /// </summary>
        /// <param name="commandText">The command text.</param>
        /// <returns>
        /// The result of the command execution.
        /// </returns>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SshOperationTimeoutException">Operation has timed out.</exception>
        public string Execute(string commandText)
        {
            CommandText = commandText;

            return Execute();
        }

        private void Session_Disconnected(object? sender, EventArgs e)
        {
            SetAsyncComplete(new SshConnectionException("An established connection was aborted by the software in your host machine.", DisconnectReason.ConnectionLost));
        }

        private void Session_ErrorOccured(object? sender, ExceptionEventArgs e)
        {
            SetAsyncComplete(e.Exception);
        }

        private void SetAsyncComplete(object result)
        {
            _ = Interlocked.CompareExchange(ref _result, result, comparand: null);

            if (_asyncResult is CommandAsyncResult asyncResult)
            {
                SetAsyncComplete(asyncResult);
            }
        }

        private void SetAsyncComplete(CommandAsyncResult asyncResult)
        {
            UnsubscribeFromEventsAndDisposeChannel();

            OutputStream.Dispose();
            ExtendedOutputStream.Dispose();

            asyncResult.IsCompleted = true;

            _ = ((EventWaitHandle)asyncResult.AsyncWaitHandle).Set();

            if (Interlocked.Exchange(ref _callback, value: null) is AsyncCallback callback)
            {
                ThreadAbstraction.ExecuteThread(() => callback(asyncResult));
            }
        }

        private void Channel_Closed(object? sender, ChannelEventArgs e)
        {
            SetAsyncComplete(CompletedResult);
        }

        private void Channel_RequestReceived(object? sender, ChannelRequestEventArgs e)
        {
            if (e.Info is ExitStatusRequestInfo exitStatusInfo)
            {
                _exitStatus = (int)exitStatusInfo.ExitStatus;
                _haveExitStatus = true;

                Debug.Assert(!exitStatusInfo.WantReply, "exit-status is want_reply := false by definition.");
            }
            else if (e.Info is ExitSignalRequestInfo exitSignalInfo)
            {
                ExitSignal = exitSignalInfo.SignalName;

                Debug.Assert(!exitSignalInfo.WantReply, "exit-signal is want_reply := false by definition.");
            }
            else if (e.Info.WantReply && _channel?.RemoteChannelNumber is uint remoteChannelNumber)
            {
                var replyMessage = new ChannelFailureMessage(remoteChannelNumber);
                _session.SendMessage(replyMessage);
            }
        }

        private void Channel_ExtendedDataReceived(object? sender, ChannelExtendedDataEventArgs e)
        {
            ExtendedOutputStream.Write(e.Data, 0, e.Data.Length);

            if (e.DataTypeCode == 1)
            {
                _hasError = true;
            }
        }

        private void Channel_DataReceived(object? sender, ChannelDataEventArgs e)
        {
            OutputStream.Write(e.Data, 0, e.Data.Length);

            if (_asyncResult is CommandAsyncResult asyncResult)
            {
                lock (asyncResult)
                {
                    asyncResult.BytesReceived += e.Data.Length;
                }
            }
        }

        /// <summary>
        /// Unsubscribes the current <see cref="SshCommand"/> from channel events, and disposes
        /// the <see cref="_channel"/>.
        /// </summary>
        private void UnsubscribeFromEventsAndDisposeChannel()
        {
            var channel = _channel;

            if (channel is null)
            {
                return;
            }

            _channel = null;

            // unsubscribe from events as we do not want to be signaled should these get fired
            // during the dispose of the channel
            channel.DataReceived -= Channel_DataReceived;
            channel.ExtendedDataReceived -= Channel_ExtendedDataReceived;
            channel.RequestReceived -= Channel_RequestReceived;
            channel.Closed -= Channel_Closed;

            // actually dispose the channel
            channel.Dispose();
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
            {
                return;
            }

            if (disposing)
            {
                // unsubscribe from session events to ensure other objects that we're going to dispose
                // are not accessed while disposing
                _session.Disconnected -= Session_Disconnected;
                _session.ErrorOccured -= Session_ErrorOccured;

                // unsubscribe from channel events to ensure other objects that we're going to dispose
                // are not accessed while disposing
                UnsubscribeFromEventsAndDisposeChannel();

                _inputStream?.Dispose();
                _inputStream = null;

                OutputStream.Dispose();
                ExtendedOutputStream.Dispose();

                if (_asyncResult is not null && _result is null)
                {
                    // In case an operation is still running, try to complete it with an ObjectDisposedException.
                    SetAsyncComplete(new ObjectDisposedException(GetType().FullName));
                }

                _isDisposed = true;
            }
        }
    }
}
