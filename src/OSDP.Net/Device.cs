using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using OSDP.Net.Messages.ACU;
using OSDP.Net.Messages.SecureChannel;

namespace OSDP.Net;

internal class Device : IComparable<Device>
{
    private const int RetryAmount = 2;

    private readonly ConcurrentQueue<Command> _commands = new();

    private readonly SecureChannel _secureChannel = new();
    private readonly bool _useSecureChannel;
    private int _counter = RetryAmount;

    private DateTime _lastValidReply = DateTime.MinValue;

    private Command _retryCommand;

    public Device(byte address, bool useCrc, bool useSecureChannel, byte[] secureChannelKey)
    {
        _useSecureChannel = useSecureChannel;
            
        Address = address;
        MessageControl = new Control(0, useCrc, useSecureChannel);

        if (!UseSecureChannel) return;

        SecureChannelKey = secureChannelKey ?? SecurityContext.DefaultKey;
            
        IsDefaultKey = SecurityContext.DefaultKey.SequenceEqual(SecureChannelKey);
    }

    internal byte[] SecureChannelKey { get; }

    private bool IsDefaultKey { get; }

    public byte Address { get; }

    public Control MessageControl { get; }

    public bool UseSecureChannel => !IsSendingMultiMessageNoSecureChannel && _useSecureChannel;

    public bool IsSecurityEstablished => !IsSendingMultiMessageNoSecureChannel && MessageControl.HasSecurityControlBlock && _secureChannel.IsEstablished;

    public bool IsConnected => _lastValidReply + TimeSpan.FromSeconds(8) >= DateTime.UtcNow &&
                               (IsSendingMultiMessageNoSecureChannel || !MessageControl.HasSecurityControlBlock || IsSecurityEstablished);

    public bool IsSendingMultipartMessage { get; set; }

    public DateTime RequestDelay { get; set; }

    public bool IsSendingMultiMessageNoSecureChannel { get; set; }
        
    public bool IsReceivingMultipartMessage { get; set; }
        
    /// <summary>
    /// Has one or more commands waiting in the queue
    /// </summary>
    public bool HasQueuedCommand => _commands.Any();

    /// <inheritdoc />
    public int CompareTo(Device other)
    {
        if (ReferenceEquals(this, other)) return 0;
        if (ReferenceEquals(null, other)) return 1;
        return Address.CompareTo(other.Address);
    }

    /// <summary>
    /// Get the next command in the queue, setup security, or send a poll command
    /// </summary>
    /// <param name="isPolling">If false, only send commands in the queue</param>
    /// <returns>The next command always if polling, could be null if not polling</returns>
    public Command GetNextCommandData(bool isPolling)
    {
        if (_retryCommand != null)
        {
            var saveCommand = _retryCommand;
            _retryCommand = null;
            return saveCommand;
        }
            
        if (isPolling)
        {
            // Don't send clear text polling if using secure channel
            if (MessageControl.Sequence == 0 && !UseSecureChannel)
            {
                return new PollCommand(Address);
            }
                
            if (UseSecureChannel && !_secureChannel.IsInitialized)
            {
                return new SecurityInitializationRequestCommand(Address,
                    _secureChannel.ServerRandomNumber().ToArray(), IsDefaultKey);
            }

            if (UseSecureChannel && !_secureChannel.IsEstablished)
            {
                return new ServerCryptogramCommand(Address, _secureChannel.ServerCryptogram, IsDefaultKey);
            }
        }

        if (!_commands.TryDequeue(out var command) && isPolling)
        {
            return new PollCommand(Address);
        }

        return command;
    }

    public void SendCommand(Command command)
    {
        _commands.Enqueue(command);
    }

    /// <summary>
    /// Store command for retry
    /// </summary>
    /// <param name="command"></param>
    public void RetryCommand(Command command)
    {
        if (_counter-- > 0)
        {
            _retryCommand = command;
        }
        else
        {
            _retryCommand = null;
            _counter = RetryAmount;
        }
    }

    public void ValidReplyHasBeenReceived(byte sequence)
    {
        MessageControl.IncrementSequence(sequence);
            
        // It's valid once sequences are above zero
        if (sequence > 0) _lastValidReply = DateTime.UtcNow;
            
        // Reset retry counter
        _counter = RetryAmount;
    }

    public void InitializeSecureChannel(Reply reply)
    {
        var replyData = reply.ExtractReplyData.ToArray();

        _secureChannel.Initialize(replyData.Skip(8).Take(8).ToArray(),
            replyData.Skip(16).Take(16).ToArray(), SecureChannelKey);
    }

    public bool ValidateSecureChannelEstablishment(Reply reply)
    {
        if (!reply.SecureCryptogramHasBeenAccepted())
        {
            return false;
        }

        _secureChannel.Establish(reply.ExtractReplyData.ToArray());

        return true;
    }

    public ReadOnlySpan<byte> GenerateMac(ReadOnlySpan<byte> message, bool isCommand)
    {
        return _secureChannel.GenerateMac(message, isCommand);
    }

    public ReadOnlySpan<byte> EncryptData(ReadOnlySpan<byte> data)
    {
        return _secureChannel.EncryptData(data);
    }

    public IEnumerable<byte> DecryptData(ReadOnlySpan<byte> data)
    {
        return _secureChannel.DecryptData(data);
    }

    public void CreateNewRandomNumber()
    {
        _secureChannel.CreateNewRandomNumber();
    }
}