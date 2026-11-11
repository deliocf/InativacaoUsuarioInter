using InativacaoUsuarioInter.Service.Resiliencia;

namespace InativacaoUsuarioInter.Service.Arquivos;

/// <summary>
/// Classifica erros de sistema de arquivos e compartilhamento (research R-05).
/// Erros de E/S são sempre transitórios: o trabalho é preservado para o próximo ciclo.
/// </summary>
internal static class ClassificadorErrosIO
{
    private const int ErroCompartilhamento = 32;
    private const int ErroBloqueio = 33;
    private const int CaminhoRedeNaoEncontrado = 53;
    private const int NomeRedeIndisponivel = 64;
    private const int NomeRedeNaoEncontrado = 67;
    private const int RedeInacessivel = 1231;

    public static ErroClassificado? Classificar(Exception excecao) => excecao switch
    {
        UnauthorizedAccessException => new(ClasseErro.Transitoria, CategoriaErro.Permissao),
        DirectoryNotFoundException or DriveNotFoundException => new(ClasseErro.Transitoria, CategoriaErro.Conexao),
        IOException io => new(ClasseErro.Transitoria, ClassificarHResult(io.HResult)),
        _ => null,
    };

    /// <summary>Classifica pelo código Win32 contido no HResult.</summary>
    public static CategoriaErro ClassificarHResult(int hresult) => (hresult & 0xFFFF) switch
    {
        ErroCompartilhamento or ErroBloqueio => CategoriaErro.Bloqueio,
        CaminhoRedeNaoEncontrado or NomeRedeIndisponivel or NomeRedeNaoEncontrado or RedeInacessivel => CategoriaErro.Conexao,
        5 => CategoriaErro.Permissao,
        _ => CategoriaErro.Desconhecida,
    };
}
