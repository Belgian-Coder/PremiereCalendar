namespace PremiereCalendar.Services;

public enum ToastKind
{
    Success,
    Error,
    Info
}

public sealed record ToastMessage(Guid Id, ToastKind Kind, string Title, string Message);
