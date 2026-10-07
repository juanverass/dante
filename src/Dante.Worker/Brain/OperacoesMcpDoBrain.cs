using System.Text.Json;
using Dante.Application.BuscaDoBrain;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.ConversaDoBrain;
using Dante.Application.Conhecimentos;
using Dante.Application.ContextosDeTrabalho;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.ContextosDeTrabalho;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Worker.Telegram;

namespace Dante.Worker.Brain;

// Adapter: chama os casos de uso da Application. Identidade e prova são fornecidas pelo Worker.
public sealed class OperacoesMcpDoBrain(IServiceScopeFactory scopes, RegistroDeOperacoesBrain registro, TelegramBrain brain)
{
    public async Task<object?> ExecutarAsync(TelegramBrain.EscopoBrainDaSessao escopo, TelegramMessage mensagem,
        string sessao, string agente, string nome, JsonElement args, CancellationToken ct = default)
    {
        ServidorMcpDoBrain.Validar(nome, args);
        await using var scope = scopes.CreateAsyncScope(); var servicos = scope.ServiceProvider; var acesso = escopo.Acesso;
        servicos.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(escopo.Identidade, acesso);
        var leitura = servicos.GetRequiredService<LeituraDoBrainAppService>(); await leitura.ValidarAcessoAsync(acesso, ct);
        var captura = servicos.GetRequiredService<ICapturaDeConhecimentoAppService>();
        var prova = Prova(mensagem, acesso.IdUsuario, escopo.Conversa, sessao, agente);
        var geracao = registro.VersaoConversa(mensagem);
        // Uma proposta MCP substitui a intenção anterior; confirmar de novo não pode consumir uma proposta natural antiga.
        if (!ServidorMcpDoBrain.Ferramentas.Single(f => f.Nome == nome).Leitura)
            servicos.GetRequiredService<IEstadoDeConversaDoBrain>().LimparConversa(escopo.Identidade, escopo.Conversa);
        switch (nome)
        {
            case "brain_obter_escopo": return new { espaco = escopo.Espaco, projeto = escopo.Projeto, sessao };
            case "brain_buscar_conhecimento":
                return await servicos.GetRequiredService<BuscaDoBrainAppService>().BuscarAsync(acesso, new()
                { Texto = Texto(args, "texto", 2000), Limite = Limite(args), Deslocamento = Deslocamento(args), Tipo = OpcionalEnum<TipoDeConhecimento>(args, "tipo"), Tags = Lista(args, "tags") }, ct);
            case "brain_listar_candidatos":
                var candidatos = await captura.ListarPendentesAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, Limite(args), ct, Deslocamento(args));
                var pagina = candidatos.Where(x => Permitida(x.Sensibilidade)).Select(Resumir).ToArray();
                return new { candidatos = pagina, limite = Limite(args), deslocamento = Deslocamento(args) };
            case "brain_mostrar_origem":
                var origemDaConsulta = Texto(args, "origem", 20);
                if (origemDaConsulta is not ("candidato" or "conhecimento")) throw new ArgumentException("Origem inválida.");
                if (origemDaConsulta == "candidato") return (await CandidatoAsync()).Historico;
                var conhecimento = await ConhecimentoAsync(Id(args, "id"));
                return new { conhecimento.Proveniencia, conhecimento.Historico };
            case "brain_capturar_conhecimento":
                var dto = Captura(args, acesso, prova);
                // Exatamente o conteúdo da mensagem (ou trecho selecionado) pode ser declarado como dito pelo usuário.
                if (dto.Natureza != NaturezaDoConteudo.ConclusaoDoAgente &&
                    !(mensagem.Text?.Contains(dto.Conteudo, StringComparison.Ordinal) == true || mensagem.Caption?.Contains(dto.Conteudo, StringComparison.Ordinal) == true || mensagem.ReplyToMessage?.Text?.Contains(dto.Conteudo, StringComparison.Ordinal) == true))
                    throw new ArgumentException("Conteúdo não consta da mensagem de origem; classifique como conclusão do agente.");
                var salvo = await captura.CapturarAsync(dto, ct);
                return new { candidato = Resumir(salvo), possivelDuplicidade = salvo.Revisao > 1 || salvo.Estado != EstadoDoCandidato.Pendente, confirmado = false };
            case "brain_corrigir_candidato":
                var anterior = await CandidatoAsync(); var correcao = Captura(args, acesso, prova) with
                {
                    Titulo = args.TryGetProperty("titulo", out _) ? Texto(args, "titulo", 300) : anterior.Titulo,
                    Tags = args.TryGetProperty("tags", out _) ? Lista(args, "tags") : anterior.Tags ?? [],
                    Tipo = anterior.Natureza == NaturezaDoConteudo.ConclusaoDoAgente ? TipoDeConhecimento.Inferencia : OpcionalEnum<TipoDeConhecimento>(args, "tipo") ?? anterior.Tipo,
                    Sensibilidade = OpcionalEnum<Sensibilidade>(args, "sensibilidade") ?? anterior.Sensibilidade
                };
                if (correcao.Natureza != anterior.Natureza) throw new ArgumentException("Natureza não pode mudar.");
                return Resumir(await captura.CorrigirAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, anterior.Id, Inteiro(args, "revisao"), correcao, ct));
            case "brain_confirmar_candidato":
            case "brain_cancelar_candidato":
                var alvo = await CandidatoAsync();
                if (alvo.Estado != EstadoDoCandidato.Pendente || alvo.Revisao != Inteiro(args, "revisao")) throw new InvalidOperationException("Revisão mudou.");
                // A closure só roda após mensagem autenticada pelo adapter, em novo scope/UoW.
                registro.Propor(mensagem, sessao, async (confirmacao, cancelamento) =>
                {
                    if (registro.VersaoConversa(confirmacao) != geracao || await brain.ResolverEscopoMcpAsync(confirmacao, cancelamento) != escopo) throw new UnauthorizedAccessException("Escopo mudou.");
                    await using var confirmScope = scopes.CreateAsyncScope(); var services = confirmScope.ServiceProvider;
                    services.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(escopo.Identidade, acesso);
                    await services.GetRequiredService<LeituraDoBrainAppService>().ValidarAcessoAsync(acesso, cancelamento);
                    var app = services.GetRequiredService<ICapturaDeConhecimentoAppService>();
                    var responsavel = Prova(confirmacao, acesso.IdUsuario, escopo.Conversa, sessao, agente);
                    if (nome == "brain_cancelar_candidato") await app.DescartarAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, alvo.Id, alvo.Revisao, responsavel, cancelamento);
                    else await app.ConfirmarAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, alvo.Id, alvo.Revisao, responsavel, cancelamento);
                    return nome == "brain_cancelar_candidato" ? "Candidato descartado com auditoria." : "Candidato consolidado com proveniência; inferências permanecem inferidas.";
                });
                return new { candidato = Resumir(alvo), aguardandoConfirmacao = true, instrucao = "Mostre a proposta ao usuário. Ele deve enviar confirmar no Telegram (ou cancelar). Nenhuma consolidação/descarte foi aplicada." };
            case "brain_criar_relacao":
                var origem = await ConhecimentoAsync(Id(args, "origem")); var destino = await ConhecimentoAsync(Id(args, "destino"));
                if (origem.Revisao != Inteiro(args, "revisao_origem") || destino.Revisao != Inteiro(args, "revisao_destino")) throw new InvalidOperationException("Revisão mudou.");
                var tipo = ObrigatorioEnum<TipoDeRelacao>(args, "tipo");
                registro.Propor(mensagem, sessao, async (confirmacao, cancelamento) =>
                {
                    if (registro.VersaoConversa(confirmacao) != geracao || await brain.ResolverEscopoMcpAsync(confirmacao, cancelamento) != escopo) throw new UnauthorizedAccessException("Escopo mudou.");
                    await using var confirmScope = scopes.CreateAsyncScope(); var services = confirmScope.ServiceProvider;
                    services.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(escopo.Identidade, acesso);
                    var l = services.GetRequiredService<LeituraDoBrainAppService>(); await l.ValidarAcessoAsync(acesso, cancelamento);
                    foreach (var item in new[] { origem, destino })
                    {
                        var atual = await l.LerAsync(item.Id, acesso, FinalidadeDeLeitura.Busca, cancelamento);
                        if (atual is null || atual.ConteudoProtegido || atual.Revisao != item.Revisao) throw new InvalidOperationException("Alvo mudou.");
                    }
                    await services.GetRequiredService<IRelacaoDeConhecimentoAppService>().RelacionarAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, origem.Id, destino.Id, tipo,
                        Prova(confirmacao, acesso.IdUsuario, escopo.Conversa, sessao, agente), cancelamento);
                    return "Relação explícita registrada com proveniência.";
                });
                return new { origem = origem.Conteudo, destino = destino.Conteudo, tipo = tipo.ToString(), aguardandoConfirmacao = true, instrucao = "Peça confirmar no Telegram; nenhuma relação foi criada ainda." };
            case "brain_listar_relacoes":
                await ConhecimentoAsync(Id(args, "id"));
                return await servicos.GetRequiredService<IRelacaoDeConhecimentoAppService>().ConsultarVizinhancaAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, Id(args, "id"), 1, Limite(args), ct);
            case "brain_obter_contexto_de_trabalho": return await servicos.GetRequiredService<ContextoDeTrabalhoAppService>().RetomarAsync(acesso, ct);
            case "brain_atualizar_contexto_de_trabalho":
                var contextos = servicos.GetRequiredService<ContextoDeTrabalhoAppService>();
                var snapshot = await contextos.RetomarAsync(acesso, ct);
                var dados = new DadosDoContexto(Texto(args, "objetivo", 2000), Texto(args, "tarefa", 2000), Texto(args, "progresso", 2000), snapshot?.Dados.IdsDecisoesConfirmadas ?? [],
                    args.TryGetProperty("referencias", out _) ? Lista(args, "referencias") : snapshot?.Dados.Referencias ?? [],
                    args.TryGetProperty("pendencias", out _) ? Lista(args, "pendencias") : snapshot?.Dados.Pendencias ?? [],
                    args.TryGetProperty("proximos_passos", out _) ? Lista(args, "proximos_passos") : snapshot?.Dados.ProximosPassos ?? [], Texto(args, "ultimo_resultado", 2000));
                return await contextos.SubstituirAsync(acesso, Inteiro(args, "revisao"), dados, snapshot?.Sensibilidade ?? Sensibilidade.Pessoal,
                    $"{prova.Origem};{prova.ReferenciaDaFonte}", snapshot?.ExpiraEm, ct);
            default: throw new ArgumentException("Ferramenta desconhecida.");
        }
        async Task<CandidatoDeConhecimentoDto> CandidatoAsync()
        {
            var candidato = await captura.ObterCandidatoAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, Id(args, "id"), ct);
            return Permitida(candidato.Sensibilidade) ? candidato : throw new UnauthorizedAccessException("Candidato não permitido.");
        }
        async Task<ConhecimentoDto> ConhecimentoAsync(Guid id)
        {
            var permitido = await leitura.LerAsync(id, acesso, FinalidadeDeLeitura.Busca, ct);
            if (permitido is null || permitido.ConteudoProtegido || permitido.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido) throw new UnauthorizedAccessException("Conhecimento não permitido.");
            return await servicos.GetRequiredService<IConhecimentoAppService>().ObterPorIdAsync(id, ct) ?? throw new ArgumentException("Conhecimento não encontrado.");
        }
    }
    private static bool Permitida(Sensibilidade s) => s is Sensibilidade.Publico or Sensibilidade.Pessoal or Sensibilidade.Trabalho;
    private static object Resumir(CandidatoDeConhecimentoDto c) => new { c.Id, c.Titulo, c.Conteudo, c.Tags, tipo = c.Tipo.ToString(), natureza = c.Natureza.ToString(), estado = c.Estado.ToString(), c.Revisao, c.IdConhecimento };
    private static ProvenienciaDto Prova(TelegramMessage m, Guid usuario, string conversa, string sessao, string agente) => new()
    {
        IdResponsavel = usuario, Origem = $"MCP/agente:{agente};sessao:{sessao}", ReferenciaDaFonte = $"conversa:{conversa}:{m.MessageId}",
        // Seleção factual de evidência, nunca transcript integral.
        TrechoDaFonte = m.Text is "confirmar" or "confirme" ? m.Text : "Operação solicitada nesta mensagem; conteúdo selecionado registrado no candidato."
    };
    private static CapturaDeConhecimentoDto Captura(JsonElement a, AcessoAoBrain acesso, ProvenienciaDto prova)
    {
        var conteudo = Texto(a, "conteudo", 10_000);
        var sensibilidade = OpcionalEnum<Sensibilidade>(a, "sensibilidade") ?? Sensibilidade.Pessoal;
        if (!Permitida(sensibilidade)) throw new UnauthorizedAccessException("Sensibilidade não permitida para agente.");
        return new() { IdEspacoDeConhecimento = acesso.IdEspacoDeConhecimento, IdProjeto = acesso.IdProjeto,
            Titulo = a.TryGetProperty("titulo", out _) ? Texto(a, "titulo", 300) : null, Conteudo = conteudo, Tags = Lista(a, "tags"),
            Tipo = OpcionalEnum<TipoDeConhecimento>(a, "tipo") ?? TipoDeConhecimento.Nota, Natureza = ObrigatorioEnum<NaturezaDoConteudo>(a, "natureza"),
            Modo = ModoDeCaptura.Explicita, Sensibilidade = sensibilidade, Justificativa = Texto(a, "justificativa", 2000), Proveniencia = prova with { TrechoDaFonte = conteudo } };
    }
    private static Guid Id(JsonElement a, string campo) => Guid.TryParse(Texto(a, campo, 40), out var id) && id != Guid.Empty ? id : throw new ArgumentException("Identificador inválido.");
    private static int Inteiro(JsonElement a, string campo) => a.GetProperty(campo).TryGetInt32(out var n) && n >= 0 ? n : throw new ArgumentException("Número inválido.");
    private static int Limite(JsonElement a) => a.TryGetProperty("limite", out _) ? Inteiro(a, "limite") is > 0 and <= 25 ? Inteiro(a, "limite") : throw new ArgumentException("Limite entre 1 e 25.") : 10;
    private static int Deslocamento(JsonElement a) => a.TryGetProperty("deslocamento", out _) ? Inteiro(a, "deslocamento") is <= 10000 ? Inteiro(a, "deslocamento") : throw new ArgumentException("Deslocamento máximo 10000.") : 0;
    private static string Texto(JsonElement a, string campo, int maximo)
    { var texto = a.GetProperty(campo).GetString(); return !string.IsNullOrWhiteSpace(texto) && texto.Length <= maximo ? texto : throw new ArgumentException("Texto inválido."); }
    private static string[] Lista(JsonElement a, string campo)
    {
        if (!a.TryGetProperty(campo, out var lista)) return [];
        if (lista.ValueKind != JsonValueKind.Array || lista.GetArrayLength() > 50) throw new ArgumentException("Lista inválida.");
        return lista.EnumerateArray().Select(x => x.GetString() is { Length: > 0 and <= 100 } s ? s : throw new ArgumentException("Item inválido.")).ToArray();
    }
    private static T ObrigatorioEnum<T>(JsonElement a, string campo) where T : struct, Enum =>
        Enum.TryParse<T>(Texto(a, campo, 80), true, out var valor) && Enum.IsDefined(valor) ? valor : throw new ArgumentException("Classificação inválida.");
    private static T? OpcionalEnum<T>(JsonElement a, string campo) where T : struct, Enum => a.TryGetProperty(campo, out _) ? ObrigatorioEnum<T>(a, campo) : null;
}
