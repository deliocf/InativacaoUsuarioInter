namespace InativacaoUsuarioInter.Service.Arquivos;

/// <summary>
/// Nomes fixos das pastas abaixo de <c>FolderPathInter</c>/<c>FolderPathCondominio</c> (feature 002, FR-001, research R-08).
/// Únicos literais de nome de pasta do serviço.
/// </summary>
internal static class NomesPastas
{
    public const string Input = "Input";
    public const string Output = "Output";
    public const string Processed = "Processed";
    public const string Logs = "Logs";
    public const string Fail = "Fail";

    /// <summary>Nome do log contratual em <c>Logs/</c> (auditoria) e <c>Fail/</c> (falhas).</summary>
    public const string NomeLog = "log_inativacao.log";
}

/// <summary>
/// Nomes antigos (feature 001), usados somente pela migração automática (FR-004, <see cref="MigradorLegado"/>).
/// </summary>
internal static class NomesPastasLegado
{
    public const string Imput = "Imput";
    public const string Auditoria = "Auditoria";
    public const string Processados = "Processados";
    public const string Falhas = "Falhas";
}
