using InativacaoUsuarioInter.Service.Resiliencia;
using Microsoft.Data.SqlClient;

namespace InativacaoUsuarioInter.Service.Inativacao;

/// <summary>
/// Classifica erros do SQL Server (research R-05). Todo erro de infraestrutura é
/// <see cref="ClasseErro.Transitoria"/>: o registro permanece pendente e a saúde é sinalizada.
/// Somente causas atribuíveis ao dado são definitivas, e essas não chegam como exceção.
/// </summary>
internal static class ClassificadorErrosSql
{
    private static readonly HashSet<int> Timeout = [-2];

    private static readonly HashSet<int> Conexao =
        [53, 64, 121, 233, 1231, 4060, 10053, 10054, 10060, 10061, 11001, 40197, 40501, 40613, 49918, 49919, 49920];

    private static readonly HashSet<int> Bloqueio = [1205, 1222];

    private static readonly HashSet<int> Configuracao = [18456, 208, 229, 230];

    public static ErroClassificado? Classificar(Exception excecao) =>
        excecao is SqlException sql ? ClassificarNumero(sql.Number) : null;

    /// <summary>Classifica pelo número do erro SQL.</summary>
    public static ErroClassificado ClassificarNumero(int numero)
    {
        var categoria = numero switch
        {
            _ when Timeout.Contains(numero) => CategoriaErro.Timeout,
            _ when Conexao.Contains(numero) => CategoriaErro.Conexao,
            _ when Bloqueio.Contains(numero) => CategoriaErro.Bloqueio,
            _ when Configuracao.Contains(numero) => CategoriaErro.Configuracao,
            _ => CategoriaErro.Desconhecida,
        };

        return new ErroClassificado(ClasseErro.Transitoria, categoria);
    }
}
