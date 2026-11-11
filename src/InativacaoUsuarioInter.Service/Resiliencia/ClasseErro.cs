namespace InativacaoUsuarioInter.Service.Resiliencia;

/// <summary>Classe de um erro (Princípio II): transitório é reprocessado; definitivo é registrado e não repetido.</summary>
public enum ClasseErro
{
    Transitoria,
    Definitiva,
}

/// <summary>Categoria do erro, exposta em <c>saude.json</c> sem mensagens brutas.</summary>
public enum CategoriaErro
{
    Timeout,
    Conexao,
    Permissao,
    Configuracao,
    Bloqueio,
    Desconhecida,
}

/// <summary>Classificação de uma exceção.</summary>
public readonly record struct ErroClassificado(ClasseErro Classe, CategoriaErro Categoria);
