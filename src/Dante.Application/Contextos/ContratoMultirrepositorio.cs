namespace Dante.Application.Contextos;

// Instruções que acompanham uma execução com repositórios vinculados (AD-58). O D.A.N.T.E. não executa git nem gh:
// branches e PRs são do agente, que recebe aqui os checkouts autorizados e as regras de entrega por repositório.
public static class ContratoMultirrepositorio
{
    // Null sem vinculados: execuções de um repositório só e General Mode não recebem nada novo.
    public static string? Para(JobExecutionContext contexto)
    {
        if (contexto.Mode != JobExecutionMode.Repository || contexto.RepositoriosVinculados.Count == 0) return null;
        var checkouts = string.Join('\n', contexto.RepositoriosVinculados
            .Select(vinculado => $"- vinculado: {vinculado.Alias} em {vinculado.Caminho}")
            .Prepend($"- principal: {contexto.RepositoryAlias} em {contexto.WorkingDirectory} (diretório de trabalho)"));
        return $"""
            Contexto multirrepositório escolhido pelo usuário no D.A.N.T.E. (vínculo explícito, nunca inferido):
            {checkouts}
            Você pode ler e alterar somente esses checkouts; outros diretórios continuam fora desta tarefa.
            Variáveis e segredos configurados no D.A.N.T.E. são só do principal: não presuma os do vinculado.
            Quando a tarefa atravessar repositórios, mantenha os contratos compartilhados (rotas, DTOs, payloads, tipos)
            consistentes dos dois lados e rode as validações e testes disponíveis em cada checkout alterado, relatando o
            resultado por repositório, sem misturar caminhos, logs ou resultados.
            Ao versionar, por pedido do usuário ou pelas instruções do próprio repositório: siga as regras de cada
            repositório; use uma branch própria em cada checkout alterado e abra o PR no remoto de cada um; no corpo de
            cada PR, referencie o PR do outro repositório e resuma o contrato compartilhado e a dependência de integração.
            Não crie branch nem PR em repositório sem alterações e nunca faça merge.
            Se uma etapa falhar em um repositório (teste, commit, push ou PR), informe por repositório o que foi
            concluído e o que falta para retomar, sem citar como existente um PR que não foi aberto.
            """;
    }

    // One-shot não tem prompt de sistema comum às duas CLIs: o contrato vai antes do pedido, separado dele.
    public static string NoPedido(JobExecutionContext contexto, string pedido) =>
        Para(contexto) is { } contrato ? $"{contrato}\nPedido do usuário:\n{pedido}" : pedido;
}
