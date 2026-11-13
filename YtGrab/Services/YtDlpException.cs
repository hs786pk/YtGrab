namespace YtGrab.Services;

/// <summary>Exception whose message is safe to show to end users.</summary>
public sealed class YtDlpException(string message, Exception? inner = null) : Exception(message, inner);