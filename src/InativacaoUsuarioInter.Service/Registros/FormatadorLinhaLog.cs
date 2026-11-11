using System.Globalization;
using InativacaoUsuarioInter.Service.Entrada.Modelos;
using InativacaoUsuarioInter.Service.Inativacao;

namespace InativacaoUsuarioInter.Service.Registros;

/// <summary>Causas do log de Falhas (FR-020).</summary>
internal static class CausasFalha
{
    public const string CpfNaoEncontrado = "CPF_NAO_ENCONTRADO";
    public const string DadoInvalido = "DADO_INVALIDO";
    public const string ArquivoInvalido = "ARQUIVO_INVALIDO";
    public const string ArquivoExcedeLimite = "ARQUIVO_EXCEDE_LIMITE";
}

/// <summary>
/// Layout contratual dos logs de Auditoria e Falhas (FR-019, FR-020, FR-020a, contracts/logs-auditoria-falhas.md).
/// <c>\r</c>, <c>\n</c> e <c>|</c> em valores viram espaço, para que nenhum conteúdo de arquivo altere o layout.
/// </summary>
internal static class FormatadorLinhaLog
{
    public const string StatusInativado = "INATIVADO";
    public const string StatusJaInativo = "JA_INATIVO";

    public static string FormatarDataHora(DateTimeOffset dataHora) =>
        dataHora.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public static string FormatarAuditoria(
        DateTimeOffset dataHora, Instancia instancia, string arquivo, string cpf, string idResponsavel, string nomeResponsavel, string status) =>
        string.Concat(
            FormatarDataHora(dataHora), " | ", instancia.ToString(),
            " | arquivo=", Sanear(arquivo),
            " | cpf=", Sanear(cpf),
            " | id_responsavel=", Sanear(idResponsavel),
            " | nome_responsavel=", Sanear(nomeResponsavel),
            " | status=", Sanear(status));

    public static string FormatarFalha(DateTimeOffset dataHora, Instancia instancia, string arquivo, string? cpf, string causa) =>
        string.Concat(
            FormatarDataHora(dataHora), " | ", instancia.ToString(),
            " | arquivo=", Sanear(arquivo),
            " | cpf=", Sanear(cpf),
            " | causa=", Sanear(causa));

    public static string Status(ResultadoRegistro resultado) => resultado switch
    {
        ResultadoRegistro.Inativado => StatusInativado,
        ResultadoRegistro.JaInativo => StatusJaInativo,
        _ => throw new ArgumentOutOfRangeException(nameof(resultado), resultado, "Resultado sem auditoria."),
    };

    public static string Sanear(string? valor) =>
        valor is null ? string.Empty : valor.Replace('\r', ' ').Replace('\n', ' ').Replace('|', ' ');
}
