using System.Runtime.InteropServices;
using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.SyncEngine;

public class NotificationHandler
{
    private readonly SyncStateDb _db;
    private readonly PathMapper _pathMapper;
    private readonly ILogger<NotificationHandler> _logger;
    private CF_CONNECTION_KEY _connectionKey;

    public NotificationHandler(
        SyncStateDb db,
        PathMapper pathMapper,
        ILogger<NotificationHandler> logger)
    {
        _db = db;
        _pathMapper = pathMapper;
        _logger = logger;
    }

    public void SetConnectionKey(CF_CONNECTION_KEY key) => _connectionKey = key;

    public Task HandleNotifyDeleteAsync(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        var fullPath = callbackInfo.NormalizedPath;
        _logger.LogInformation("NOTIFY_DELETE notification: {Path}", fullPath);

        // NOTE: Do NOT delete the DB entry here.
        // The UploadManager.HandleDeleteAsync needs the DB entry to look up the
        // remote path for the WebDAV DELETE request. If we delete the DB entry here
        // (in the cfapi callback), the UploadManager may not find it and silently
        // skip the remote delete, leaving orphaned files on the server.
        // UploadManager handles both the WebDAV DELETE and the DB cleanup.

        // Always acknowledge — refusing will block Explorer
        AcknowledgeDelete(callbackInfo, new NTStatus(0));
        return Task.CompletedTask;
    }

    public Task HandleNotifyRenameAsync(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        var fullPath = callbackInfo.NormalizedPath;
        _logger.LogInformation("NOTIFY_RENAME notification: {Path}", fullPath);

        // Always acknowledge — refusing will block Explorer
        AcknowledgeRename(callbackInfo, new NTStatus(0));
        return Task.CompletedTask;
    }

    private void AcknowledgeDelete(CF_CALLBACK_INFO callbackInfo, NTStatus status)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE,
            ConnectionKey = _connectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey
        };

        var ad = new CF_OPERATION_PARAMETERS.ACKDELETE
        {
            Flags = CF_OPERATION_ACK_DELETE_FLAGS.CF_OPERATION_ACK_DELETE_FLAG_NONE,
            CompletionStatus = status
        };

        var opParams = CF_OPERATION_PARAMETERS.Create(ad);

        var hr = CfExecute(opInfo, ref opParams);
        if (hr.Failed)
            _logger.LogError("CfExecute ACK_DELETE failed: {Hr}", hr);
    }

    private void AcknowledgeRename(CF_CALLBACK_INFO callbackInfo, NTStatus status)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME,
            ConnectionKey = _connectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey
        };

        var ad = new CF_OPERATION_PARAMETERS.ACKRENAME
        {
            Flags = CF_OPERATION_ACK_RENAME_FLAGS.CF_OPERATION_ACK_RENAME_FLAG_NONE,
            CompletionStatus = status
        };

        var opParams = CF_OPERATION_PARAMETERS.Create(ad);

        var hr = CfExecute(opInfo, ref opParams);
        if (hr.Failed)
            _logger.LogError("CfExecute ACK_RENAME failed: {Hr}", hr);
    }
}
