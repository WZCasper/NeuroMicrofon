using System;

namespace NeuroMicrophone.Obs;

/// <summary>
/// Ошибка работы с OBS, текст которой уже безопасно показывать пользователю
/// как есть (на русском, с понятной подсказкой, что делать). Остальные
/// исключения (баги самого кода) сюда не заворачиваются — их ловит общий
/// обработчик приложения, чтобы настоящая причина не пряталась за "красивым"
/// сообщением.
/// </summary>
public class ObsException : Exception
{
    public ObsException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>OBS закрыл соединение (штатно или из-за ошибки протокола) — с кодом закрытия WebSocket.</summary>
public sealed class ObsConnectionClosedException : ObsException
{
    public int? CloseCode { get; }

    public ObsConnectionClosedException(int? closeCode, string? description)
        : base($"OBS закрыл соединение (код {(closeCode?.ToString() ?? "?")}){(string.IsNullOrWhiteSpace(description) ? "" : ": " + description)}.")
    {
        CloseCode = closeCode;
    }
}

/// <summary>OBS получил запрос, но отклонил его (requestStatus.result == false).</summary>
public sealed class ObsRequestException : ObsException
{
    /// <summary>RequestStatus::ResourceNotFound из протокола obs-websocket — например, источника с таким именем нет.</summary>
    public const int ResourceNotFoundCode = 600;

    public string RequestType { get; }
    public int Code { get; }
    public string? Comment { get; }

    public ObsRequestException(string requestType, int code, string? comment)
        : base($"OBS отклонил запрос {requestType} (код {code}){(string.IsNullOrWhiteSpace(comment) ? "" : ": " + comment)}")
    {
        RequestType = requestType;
        Code = code;
        Comment = comment;
    }
}
