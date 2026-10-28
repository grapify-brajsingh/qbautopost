namespace QbAutopost.Api.Endpoints;

/// <summary>
/// Dashboard API v2 (T-1109): which surface a shared handler is answering for. <see cref="V1"/> is the default
/// everywhere, so a handler mapped without a flavor behaves exactly as it did before <c>/api/v2</c> existed (O-1).
/// </summary>
public enum ApiFlavor
{
    V1,
    V2,
}
