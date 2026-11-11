using InativacaoUsuarioInter.Service;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Configuracao;

// Registro do Windows Service (custom action do MSI ou operação): InativacaoUsuarioInter.Service.exe --instalar | --desinstalar
if (InstaladorServico.EhComando(args))
{
    return OperatingSystem.IsWindows() ? InstaladorServico.Executar(args) : 2;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "InativacaoUsuarioInter");
builder.Services.Configure<HostOptions>(builder.Configuration.GetSection("HostOptions"));
builder.Services.AdicionarInativacao(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddHostedService<CicloWorker>();
builder.AdicionarTelemetria();

using var host = builder.Build();

// Configuração inválida: o serviço não inicia (FR-004, CA-18). Todas as falhas são reunidas numa mensagem
// que cita chave e regra e nunca contém valores de ConnectionString:* (FR-037). Validado antes do RunAsync,
// enquanto o logger ainda está ativo; o ValidateOnStart permanece como segunda barreira.
var falhas = ValidacaoPartida.Validar(host.Services);
if (falhas.Count > 0)
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InativacaoUsuarioInter");
    ProgramaLog.ConfiguracaoInvalida(logger, string.Join(" | ", falhas));
    return 1;
}

await host.RunAsync();
return 0;

/// <summary>Mensagens de log do ponto de entrada.</summary>
internal static partial class ProgramaLog
{
    [LoggerMessage(Level = LogLevel.Critical, Message = "Configuração inválida; o serviço não foi iniciado: {Falhas}")]
    public static partial void ConfiguracaoInvalida(ILogger logger, string falhas);
}
