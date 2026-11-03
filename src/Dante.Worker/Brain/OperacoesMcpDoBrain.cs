using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.BuscaDoBrain;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.ConversaDoBrain;
using Dante.Application.Conhecimentos;
using Dante.Application.ContextosDeTrabalho;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Application.QualidadeDoBrain;
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
        if (!registro.ConversaPermitida(sessao, mensagem)) throw ErroDoBrainMcp.EscopoMudou();
        ServidorMcpDoBrain.Validar(nome, args);
        await using var scope = scopes.CreateAsyncScope(); var servicos = scope.ServiceProvider; var acesso = escopo.Acesso;
        servicos.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(escopo.Identidade, acesso);
        var leitura = servicos.GetRequiredService<LeituraDoBrainAppService>();
        // O acesso vem do escopo da sessão, não de argumentos: espaço/projeto arquivado ou removido é mudança de escopo.
        try { await leitura.ValidarAcessoAsync(acesso, ct); } catch (UnauthorizedAccessException) { throw ErroDoBrainMcp.EscopoMudou(); }
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
                { Texto = Texto(args, "texto", 2000), Limite = Limite(args), Deslocamento = Deslocamento(args), Tipo = OpcionalEnum<TipoDeConhecimento>(args, "tipo"), NaturezaDeOrigem = OpcionalEnum<NaturezaDoConteudo>(args, "natureza"), Tags = Lista(args, "tags") }, ct);
            case "brain_listar_candidatos":
                var candidatos = await captura.ListarPendentesAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, Limite(args), ct, Deslocamento(args),
                    Lista(args, "tags"), args.TryGetProperty("texto", out _) ? Texto(args, "texto", 2000) : null);
                var pagina = candidatos.Where(x => Permitida(x.Sensibilidade)).Select(Resumir).ToArray();
                return new { candidatos = pagina, limite = Limite(args), deslocamento = Deslocamento(args) };
            case "brain_mostrar_origem":
                var origemDaConsulta = Texto(args, "origem", 20);
                if (origemDaConsulta is not ("candidato" or "conhecimento")) throw ErroDoBrainMcp.Entrada();
                if (origemDaConsulta == "candidato") return (await CandidatoAsync()).Historico;
                var conhecimento = await ConhecimentoAsync(Id(args, "id"));
                return new { conhecimento.Id, conhecimento.Tipo, conhecimento.NaturezaDeOrigem, conhecimento.Natureza, conhecimento.Status, conhecimento.Rotulo, conhecimento.Proveniencia, conhecimento.Historico };
            case "brain_capturar_conhecimento":
                var dto = Captura(args, acesso, ProvaDaFonte(prova, mensagem, args));
                ValidarOrigem(dto, mensagem);
                var salvo = await captura.CapturarAsync(dto, ct);
                return new { candidato = Resumir(salvo), possivelDuplicidade = salvo.Revisao > 1 || salvo.Estado != EstadoDoCandidato.Pendente, confirmado = false };
            case "brain_corrigir_candidato":
                var anterior = await CandidatoAsync();
                if (anterior.Estado != EstadoDoCandidato.Pendente || anterior.Revisao != Inteiro(args, "revisao")) throw ErroDoBrainMcp.Revisao();
                var correcao = Captura(args, acesso, ProvaDaFonte(prova, mensagem, args)) with
                {
                    Titulo = args.TryGetProperty("titulo", out _) ? Texto(args, "titulo", 300) : anterior.Titulo,
                    Tags = args.TryGetProperty("tags", out _) ? Lista(args, "tags") : anterior.Tags ?? [],
                    Tipo = OpcionalEnum<TipoDeConhecimento>(args, "tipo") ?? anterior.Tipo,
                    Sensibilidade = OpcionalEnum<Sensibilidade>(args, "sensibilidade") ?? anterior.Sensibilidade
                };
                ValidarOrigem(correcao, mensagem);
                if (correcao.Natureza != anterior.Natureza) throw ErroDoBrainMcp.Entrada(ErrosDoBrainMcp.DetalheNatureza);
                return Resumir(await captura.CorrigirAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, anterior.Id, Inteiro(args, "revisao"), correcao, ct));
            case "brain_confirmar_candidato":
            case "brain_cancelar_candidato":
                var alvo = await CandidatoAsync();
                if (alvo.Estado != EstadoDoCandidato.Pendente || alvo.Revisao != Inteiro(args, "revisao")) throw ErroDoBrainMcp.Revisao();
                // A closure só roda após mensagem autenticada pelo adapter, em novo scope/UoW.
                Propor(async (confirmacao, cancelamento) =>
                {
                    await RevalidarEscopoAsync(confirmacao, cancelamento);
                    await using var confirmScope = scopes.CreateAsyncScope(); var services = confirmScope.ServiceProvider;
                    services.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(escopo.Identidade, acesso);
                    await ValidarAcessoDaConfirmacaoAsync(services.GetRequiredService<LeituraDoBrainAppService>(), cancelamento);
                    var app = services.GetRequiredService<ICapturaDeConhecimentoAppService>();
                    CandidatoDeConhecimentoDto atual;
                    try { atual = await app.ObterCandidatoAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, alvo.Id, cancelamento); }
                    catch (ArgumentException) { throw ErroDoBrainMcp.NaoEncontrado(); }
                    if (!Permitida(atual.Sensibilidade)) throw ErroDoBrainMcp.NaoEncontrado();
                    if (atual.Estado != EstadoDoCandidato.Pendente || atual.Revisao != alvo.Revisao) throw ErroDoBrainMcp.Revisao();
                    var responsavel = Prova(confirmacao, acesso.IdUsuario, escopo.Conversa, sessao, agente);
                    if (nome == "brain_cancelar_candidato")
                    {
                        await app.DescartarAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, alvo.Id, alvo.Revisao, responsavel, cancelamento);
                        return "Candidato descartado com auditoria.";
                    }
                    var id = await app.ConfirmarAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, alvo.Id, alvo.Revisao, responsavel, cancelamento);
                    var consolidado = await services.GetRequiredService<IConhecimentoAppService>().ObterPorIdAsync(id, cancelamento);
                    return consolidado?.Status switch
                    {
                        StatusDoConhecimento.Confirmado => "Candidato consolidado com sua confirmação e proveniência preservada.",
                        StatusDoConhecimento.Inferido when consolidado.NaturezaDeOrigem == NaturezaDoConteudo.ConclusaoDoAgente || consolidado.Natureza == NaturezaDoConteudo.ConclusaoDoAgente
                            => "Candidato consolidado. Conteúdo registrado como conclusão do agente, não confirmado. Sua confirmação autoriza o registro; ele permanece inferido.",
                        StatusDoConhecimento.Inferido => "Candidato consolidado. Conteúdo registrado como inferido, não confirmado.",
                        _ => "Candidato consolidado. Consulte o Brain para verificar o estado atual."
                    };
                });
                return new { candidato = Resumir(alvo), aguardandoConfirmacao = true, instrucao = "Mostre a proposta ao usuário. Ele deve enviar confirmar no Telegram (ou cancelar). Nenhuma consolidação/descarte foi aplicada." };
            case "brain_atualizar_conhecimento":
                var existente = await ConhecimentoAsync(Id(args, "id"));
                if (existente.Revisao != Inteiro(args, "revisao")) throw ErroDoBrainMcp.Revisao();
                var novaVersao = Captura(args, acesso, ProvaDaFonte(prova, mensagem, args));
                ValidarOrigem(novaVersao, mensagem);
                var sensibilidadeNova = OpcionalEnum<Sensibilidade>(args, "sensibilidade") ?? existente.Sensibilidade;
                // O item já é visível ao agente; recusar a redução não revela nada além do que ele leu.
                if (sensibilidadeNova < existente.Sensibilidade) throw ErroDoBrainMcp.Entrada(ErrosDoBrainMcp.DetalheReducaoDeSensibilidade);
                var atualizado = existente with
                {
                    Conteudo = novaVersao.Conteudo,
                    Natureza = novaVersao.Natureza,
                    Tipo = OpcionalEnum<TipoDeConhecimento>(args, "tipo") ?? existente.Tipo,
                    Sensibilidade = sensibilidadeNova,
                    Tags = args.TryGetProperty("tags", out _) ? novaVersao.Tags : existente.Tags,
                    DadosEstruturados = args.TryGetProperty("titulo", out _) ? AtualizarTitulo(existente.DadosEstruturados, novaVersao.Titulo!) : existente.DadosEstruturados,
                    Proveniencia = novaVersao.Proveniencia
                };
                ProporAlteracao([existente], async (services, confirmacao, cancelamento) =>
                {
                    var resultado = await services.GetRequiredService<IConhecimentoAppService>().CorrigirAsync(existente.Id,
                        atualizado with { Proveniencia = atualizado.Proveniencia with
                        { Origem = $"{atualizado.Proveniencia.Origem};confirmacao:{confirmacao.MessageId}" } }, cancelamento);
                    if (resultado is null) throw ErroDoBrainMcp.NaoEncontrado();
                    return "Conhecimento atualizado no mesmo ID com histórico e proveniência; a correção não herda a confirmação anterior.";
                });
                return new { existente.Id, existente.Revisao, anterior = existente.Conteudo, proposto = new { atualizado.Conteudo, atualizado.DadosEstruturados, atualizado.Tags,
                        tipo = atualizado.Tipo.ToString(), sensibilidade = atualizado.Sensibilidade.ToString() },
                    aguardandoConfirmacao = true, instrucao = "Mostre a correção e peça confirmar no Telegram; nenhuma atualização foi aplicada." };
            case "brain_consolidar_duplicatas":
                var destinoDaConsolidacao = await ConhecimentoAsync(Id(args, "id"));
                if (destinoDaConsolidacao.Revisao != Inteiro(args, "revisao")) throw ErroDoBrainMcp.Revisao();
                var listaDuplicatas = args.GetProperty("duplicatas");
                if (listaDuplicatas.ValueKind != JsonValueKind.Array || listaDuplicatas.GetArrayLength() is < 1 or > 20) throw ErroDoBrainMcp.Entrada();
                var duplicatas = new List<ConhecimentoDto>();
                foreach (var item in listaDuplicatas.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(p => p.Name is not ("id" or "revisao"))) throw ErroDoBrainMcp.Entrada();
                    // Compara IDs antes de resolver: repetir o destino é erro de entrada sem depender do alvo.
                    var idDuplicata = Id(item, "id");
                    if (idDuplicata == destinoDaConsolidacao.Id || duplicatas.Any(d => d.Id == idDuplicata)) throw ErroDoBrainMcp.Entrada();
                    var duplicata = await ConhecimentoAsync(idDuplicata);
                    if (duplicata.Revisao != Inteiro(item, "revisao")) throw ErroDoBrainMcp.Revisao();
                    duplicatas.Add(duplicata);
                }
                ProporAlteracao([destinoDaConsolidacao, .. duplicatas], async (services, confirmacao, cancelamento) =>
                {
                    await services.GetRequiredService<ManutencaoDoBrainAppService>().ConsolidarAsync(acesso,
                        new(destinoDaConsolidacao.Id, destinoDaConsolidacao.Revisao), duplicatas.Select(d => new RevisaoEsperadaDto(d.Id, d.Revisao)).ToArray(),
                        Prova(confirmacao, acesso.IdUsuario, escopo.Conversa, sessao, agente), cancelamento);
                    return "Duplicatas consolidadas: destino preservado, proveniências incorporadas e duplicatas substituídas.";
                });
                return new { destino = new { destinoDaConsolidacao.Id, destinoDaConsolidacao.Conteudo, destinoDaConsolidacao.Revisao },
                    duplicatas = duplicatas.Select(d => new { d.Id, d.Conteudo, d.Revisao }), aguardandoConfirmacao = true,
                    instrucao = "Mostre o destino e todas as duplicatas, peça confirmar no Telegram; nenhuma consolidação foi aplicada." };
            case "brain_criar_relacao":
                var tipo = ObrigatorioEnum<TipoDeRelacao>(args, "tipo");
                var idOrigem = Id(args, "origem"); var idDestino = Id(args, "destino");
                if (idOrigem == idDestino) throw ErroDoBrainMcp.Entrada();
                var origem = await ConhecimentoAsync(idOrigem); var destino = await ConhecimentoAsync(idDestino);
                if (origem.Revisao != Inteiro(args, "revisao_origem") || destino.Revisao != Inteiro(args, "revisao_destino")) throw ErroDoBrainMcp.Revisao();
                // Os dois tipos já são visíveis ao agente; a regra do domínio é aplicada antes de preparar a proposta (#249/#251).
                if (!RelacaoDeConhecimento.TiposCompativeis(tipo, origem.Tipo, destino.Tipo)) throw ErroDoBrainMcp.Entrada(ErrosDoBrainMcp.DetalheTiposDaRelacao);
                Propor(async (confirmacao, cancelamento) =>
                {
                    await RevalidarEscopoAsync(confirmacao, cancelamento);
                    await using var confirmScope = scopes.CreateAsyncScope(); var services = confirmScope.ServiceProvider;
                    services.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(escopo.Identidade, acesso);
                    var l = services.GetRequiredService<LeituraDoBrainAppService>(); await ValidarAcessoDaConfirmacaoAsync(l, cancelamento);
                    foreach (var item in new[] { origem, destino })
                    {
                        var atual = await l.LerAsync(item.Id, acesso, FinalidadeDeLeitura.Busca, cancelamento);
                        if (atual is null || atual.ConteudoProtegido) throw ErroDoBrainMcp.NaoEncontrado();
                        if (atual.Revisao != item.Revisao) throw ErroDoBrainMcp.Revisao();
                    }
                    await services.GetRequiredService<IRelacaoDeConhecimentoAppService>().RelacionarAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, origem.Id, destino.Id, tipo,
                        Prova(confirmacao, acesso.IdUsuario, escopo.Conversa, sessao, agente), cancelamento);
                    return "Relação explícita registrada com proveniência.";
                });
                return new { origem = origem.Conteudo, destino = destino.Conteudo, tipo = tipo.ToString(), aguardandoConfirmacao = true, instrucao = "Peça confirmar no Telegram; nenhuma relação foi criada ainda." };
            case "brain_listar_relacoes":
                await ConhecimentoAsync(Id(args, "id"));
                var relacoes = servicos.GetRequiredService<IRelacaoDeConhecimentoAppService>();
                var visiveis = new List<RelacaoDeConhecimentoDto>(); var deslocamento = 0; var limite = Limite(args);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var janela = await relacoes.ConsultarPaginaDeVizinhasAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, Id(args, "id"), deslocamento, 100, ct);
                    foreach (var relacao in janela.Relacoes)
                    {
                        var a = await leitura.LerAsync(relacao.IdOrigem, acesso, FinalidadeDeLeitura.Busca, ct);
                        var b = await leitura.LerAsync(relacao.IdDestino, acesso, FinalidadeDeLeitura.Busca, ct);
                        if (a is null || b is null || a.ConteudoProtegido || b.ConteudoProtegido ||
                            a.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido ||
                            b.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido) continue;
                        // O limite lógico conta apenas alvos autorizados; mais um permitido prova truncamento.
                        if (visiveis.Count == limite) return new VizinhancaDto(visiveis, true);
                        // Não expor prova bruta, inclusive de classificações históricas mais restritas.
                        visiveis.Add(relacao with { Proveniencia = new() { Origem = "proveniência protegida no canal MCP" },
                            ProvenienciaDaResolucao = relacao.ProvenienciaDaResolucao is null ? null : new() { Origem = "proveniência protegida no canal MCP" } });
                    }
                    if (!janela.LimiteAtingido) return new VizinhancaDto(visiveis, false);
                    deslocamento = checked(deslocamento + janela.Relacoes.Count);
                }
            case "brain_obter_contexto_de_trabalho": return await servicos.GetRequiredService<ContextoDeTrabalhoAppService>().RetomarAsync(acesso, ct);
            case "brain_atualizar_contexto_de_trabalho":
                var contextos = servicos.GetRequiredService<ContextoDeTrabalhoAppService>();
                var snapshot = await contextos.RetomarAsync(acesso, ct);
                if (snapshot is not null && snapshot.Revisao != Inteiro(args, "revisao")) throw ErroDoBrainMcp.Revisao();
                var idsDecisoes = snapshot?.Dados.IdsDecisoesConfirmadas ?? [];
                if (args.TryGetProperty("ids_decisoes_confirmadas", out _))
                {
                    idsDecisoes = IdsDecisoesConfirmadas(args);
                    // Preserve a resolução autorizada e a recusa sem oráculo da #251. A Application
                    // revalida tipo, confirmação, validade e classificação antes de salvar o snapshot.
                    foreach (var idDecisao in idsDecisoes.Distinct()) await ConhecimentoAsync(idDecisao);
                }
                GarantirSemCredencial([Texto(args, "objetivo", 2000), Texto(args, "tarefa", 2000), Texto(args, "progresso", 2000), Texto(args, "ultimo_resultado", 2000),
                    .. Lista(args, "referencias"), .. Lista(args, "pendencias"), .. Lista(args, "proximos_passos")]);
                var dados = new DadosDoContexto(Texto(args, "objetivo", 2000), Texto(args, "tarefa", 2000), Texto(args, "progresso", 2000), idsDecisoes,
                    args.TryGetProperty("referencias", out _) ? Lista(args, "referencias") : snapshot?.Dados.Referencias ?? [],
                    args.TryGetProperty("pendencias", out _) ? Lista(args, "pendencias") : snapshot?.Dados.Pendencias ?? [],
                    args.TryGetProperty("proximos_passos", out _) ? Lista(args, "proximos_passos") : snapshot?.Dados.ProximosPassos ?? [], Texto(args, "ultimo_resultado", 2000));
                return await contextos.SubstituirAsync(acesso, Inteiro(args, "revisao"), dados, snapshot?.Sensibilidade ?? Sensibilidade.Pessoal,
                    $"{prova.Origem};{prova.ReferenciaDaFonte}", snapshot?.ExpiraEm, ct);
            default: throw ErroDoBrainMcp.Entrada();
        }
        void Propor(Func<TelegramMessage, CancellationToken, Task<string>> executar)
        {
            try { registro.Propor(mensagem, sessao, executar); }
            catch (UnauthorizedAccessException) { throw ErroDoBrainMcp.EscopoMudou(); }
        }
        async Task RevalidarEscopoAsync(TelegramMessage confirmacao, CancellationToken cancelamento)
        {
            if (registro.VersaoConversa(confirmacao) != geracao || await brain.ResolverEscopoMcpAsync(confirmacao, cancelamento) != escopo) throw ErroDoBrainMcp.EscopoMudou();
        }
        async Task ValidarAcessoDaConfirmacaoAsync(LeituraDoBrainAppService l, CancellationToken cancelamento)
        {
            try { await l.ValidarAcessoAsync(acesso, cancelamento); } catch (UnauthorizedAccessException) { throw ErroDoBrainMcp.EscopoMudou(); }
        }
        void ProporAlteracao(IReadOnlyList<ConhecimentoDto> alvos,
            Func<IServiceProvider, TelegramMessage, CancellationToken, Task<string>> aplicar)
        {
            Propor(async (confirmacao, cancelamento) =>
            {
                await RevalidarEscopoAsync(confirmacao, cancelamento);
                await using var confirmScope = scopes.CreateAsyncScope(); var services = confirmScope.ServiceProvider;
                services.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(escopo.Identidade, acesso);
                var l = services.GetRequiredService<LeituraDoBrainAppService>();
                await ValidarAcessoDaConfirmacaoAsync(l, cancelamento);
                foreach (var alvo in alvos)
                {
                    var atual = await l.LerAsync(alvo.Id, acesso, FinalidadeDeLeitura.Busca, cancelamento);
                    if (atual is null || atual.ConteudoProtegido) throw ErroDoBrainMcp.NaoEncontrado();
                    if (atual.Revisao != alvo.Revisao || atual.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido) throw ErroDoBrainMcp.Revisao();
                }
                return await aplicar(services, confirmacao, cancelamento);
            });
        }
        // Resolução de IDs enviados pelo agente. Inexistente, outro espaço/projeto/usuário, sensibilidade não permitida,
        // conteúdo protegido e item inativo produzem a MESMA exceção, sem detalhe (#251: sem oráculo de enumeração).
        // ID do tipo errado só vira entrada_invalida quando o outro item é visível ao agente neste escopo.
        async Task<CandidatoDeConhecimentoDto> CandidatoAsync()
        {
            var id = Id(args, "id");
            CandidatoDeConhecimentoDto candidato;
            try { candidato = await captura.ObterCandidatoAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, id, ct); }
            catch (ArgumentException)
            {
                if (await ConhecimentoVisivelAsync(id) is not null) throw ErroDoBrainMcp.Entrada(ErrosDoBrainMcp.DetalheIdDeConhecimento);
                throw ErroDoBrainMcp.NaoEncontrado();
            }
            return Permitida(candidato.Sensibilidade) ? candidato : throw ErroDoBrainMcp.NaoEncontrado();
        }
        async Task<ConhecimentoDto> ConhecimentoAsync(Guid id)
        {
            if (await ConhecimentoVisivelAsync(id) is null)
            {
                if (await CandidatoVisivelAsync(id)) throw ErroDoBrainMcp.Entrada(ErrosDoBrainMcp.DetalheIdDeCandidato);
                throw ErroDoBrainMcp.NaoEncontrado();
            }
            return await servicos.GetRequiredService<IConhecimentoAppService>().ObterPorIdAsync(id, ct) ?? throw ErroDoBrainMcp.NaoEncontrado();
        }
        async Task<LeituraProtegidaDto?> ConhecimentoVisivelAsync(Guid id)
        {
            var permitido = await leitura.LerAsync(id, acesso, FinalidadeDeLeitura.Busca, ct);
            return permitido is null || permitido.ConteudoProtegido || permitido.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido ? null : permitido;
        }
        async Task<bool> CandidatoVisivelAsync(Guid id)
        {
            try { return Permitida((await captura.ObterCandidatoAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, id, ct)).Sensibilidade); }
            catch (ArgumentException) { return false; }
        }
    }
    private static string AtualizarTitulo(string? dados, string titulo)
    {
        var objeto = dados is null ? new JsonObject() : JsonNode.Parse(dados) as JsonObject ??
            throw new ArgumentException("Metadados existentes não permitem atualizar título.");
        objeto["titulo"] = titulo;
        return objeto.ToJsonString();
    }
    private static void ValidarOrigem(CapturaDeConhecimentoDto dto, TelegramMessage mensagem)
    {
        if (dto.Natureza == NaturezaDoConteudo.ConclusaoDoAgente) return;
        if (mensagem.Text?.Contains(dto.Conteudo, StringComparison.Ordinal) == true ||
            mensagem.Caption?.Contains(dto.Conteudo, StringComparison.Ordinal) == true ||
            // Mensagem citada só é origem literal se o próprio usuário a escreveu: bot, agente, outro usuário ou autor
            // ausente nunca contam como declaração dele (AD-59 I3/D1, #264).
            mensagem.ReplyToMessage is { } fonte && fonte.Chat.Id == mensagem.Chat.Id && fonte.MessageThreadId == mensagem.MessageThreadId &&
            mensagem.From is { } usuario && fonte.From?.Id == usuario.Id &&
            fonte.Text?.Contains(dto.Conteudo, StringComparison.Ordinal) == true) return;
        throw ErroDoBrainMcp.Entrada(ErrosDoBrainMcp.DetalheOrigemLiteral);
    }
    private static bool Permitida(Sensibilidade s) => s is Sensibilidade.Publico or Sensibilidade.Pessoal or Sensibilidade.Trabalho;
    private static object Resumir(CandidatoDeConhecimentoDto c) => new { c.Id, c.Titulo, c.Conteudo, c.Tags, tipo = c.Tipo.ToString(), natureza = c.Natureza.ToString(), estado = c.Estado.ToString(), c.Revisao, c.IdConhecimento };
    // S00000N recomeça a cada processo do Worker; a instância torna a sessão única na proveniência (#249).
    internal static string Origem(string agente, string instancia, string sessao) => $"MCP/agente:{agente};sessao:{instancia}:{sessao}";
    private ProvenienciaDto Prova(TelegramMessage m, Guid usuario, string conversa, string sessao, string agente) => new()
    {
        IdResponsavel = usuario, Origem = Origem(agente, brain.Instancia, sessao), ReferenciaDaFonte = $"conversa:{conversa}:{m.MessageId}",
        // Seleção factual de evidência, nunca transcript integral.
        TrechoDaFonte = m.Text is "confirmar" or "confirme" ? m.Text : "Operação solicitada nesta mensagem; conteúdo selecionado registrado no candidato."
    };
    private static ProvenienciaDto ProvaDaFonte(ProvenienciaDto prova, TelegramMessage mensagem, JsonElement args)
    {
        var conteudo = Texto(args, "conteudo", 10_000);
        if (mensagem.Text?.Contains(conteudo, StringComparison.Ordinal) == true ||
            mensagem.Caption?.Contains(conteudo, StringComparison.Ordinal) == true) return prova;
        if (mensagem.ReplyToMessage is not { } fonte || fonte.Chat.Id != mensagem.Chat.Id ||
            fonte.MessageThreadId != mensagem.MessageThreadId || fonte.Text?.Contains(conteudo, StringComparison.Ordinal) != true) return prova;
        var autoria = fonte.From?.Id == mensagem.From?.Id && fonte.From is not null ? AutoriaDaFonte.Usuario :
            fonte.From?.IsBot == true ? AutoriaDaFonte.Agente : AutoriaDaFonte.Desconhecida;
        return prova with { Origem = $"{prova.Origem};autoria-da-fonte:{autoria}" };
    }
    private static CapturaDeConhecimentoDto Captura(JsonElement a, AcessoAoBrain acesso, ProvenienciaDto prova)
    {
        var conteudo = Texto(a, "conteudo", 10_000);
        var sensibilidade = OpcionalEnum<Sensibilidade>(a, "sensibilidade") ?? Sensibilidade.Pessoal;
        // Pedido de classificação acima do teto do agente: recusa de entrada, sem consultar nenhum item.
        if (!Permitida(sensibilidade)) throw ErroDoBrainMcp.Entrada(ErrosDoBrainMcp.DetalheSensibilidade);
        GarantirSemCredencial([conteudo, Texto(a, "justificativa", 2000), a.TryGetProperty("titulo", out _) ? Texto(a, "titulo", 300) : null, .. Lista(a, "tags")]);
        return new() { IdEspacoDeConhecimento = acesso.IdEspacoDeConhecimento, IdProjeto = acesso.IdProjeto,
            Titulo = a.TryGetProperty("titulo", out _) ? Texto(a, "titulo", 300) : null, Conteudo = conteudo, Tags = Lista(a, "tags"),
            Tipo = OpcionalEnum<TipoDeConhecimento>(a, "tipo") ?? TipoDeConhecimento.Nota, Natureza = ObrigatorioEnum<NaturezaDoConteudo>(a, "natureza"),
            Modo = ModoDeCaptura.Explicita, Sensibilidade = sensibilidade, Justificativa = Texto(a, "justificativa", 2000), Proveniencia = prova with { TrechoDaFonte = conteudo } };
    }
    // Mesma detecção da Application, antecipada para devolver detalhe útil sem expor a mensagem interna.
    private static void GarantirSemCredencial(params string?[] textos)
    { if (textos.Any(ProtecaoDeSegredos.ContemSegredo)) throw ErroDoBrainMcp.Entrada(ErrosDoBrainMcp.DetalheCredencial); }
    // Parsing de argumentos: tipo JSON errado é entrada_invalida, nunca InvalidOperationException do JsonElement.
    private static JsonElement Campo(JsonElement a, string campo, JsonValueKind tipo) =>
        a.TryGetProperty(campo, out var valor) && valor.ValueKind == tipo ? valor : throw ErroDoBrainMcp.Entrada();
    private static Guid Id(JsonElement a, string campo) => Guid.TryParse(Texto(a, campo, 40), out var id) && id != Guid.Empty ? id : throw ErroDoBrainMcp.Entrada();
    private static Guid[] IdsDecisoesConfirmadas(JsonElement a)
    {
        var lista = Campo(a, "ids_decisoes_confirmadas", JsonValueKind.Array);
        if (lista.GetArrayLength() > 20) throw ErroDoBrainMcp.Entrada();
        return lista.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 and <= 40 } s &&
            Guid.TryParse(s, out var id) && id != Guid.Empty ? id : throw ErroDoBrainMcp.Entrada()).ToArray();
    }
    private static int Inteiro(JsonElement a, string campo) => Campo(a, campo, JsonValueKind.Number).TryGetInt32(out var n) && n >= 0 ? n : throw ErroDoBrainMcp.Entrada();
    private static int Limite(JsonElement a) => a.TryGetProperty("limite", out _) ? Inteiro(a, "limite") is > 0 and <= ServidorMcpDoBrain.LimiteMaximo ? Inteiro(a, "limite") : throw ErroDoBrainMcp.Entrada() : ServidorMcpDoBrain.LimitePadrao;
    private static int Deslocamento(JsonElement a) => a.TryGetProperty("deslocamento", out _) ? Inteiro(a, "deslocamento") is <= ServidorMcpDoBrain.DeslocamentoMaximo ? Inteiro(a, "deslocamento") : throw ErroDoBrainMcp.Entrada() : 0;
    private static string Texto(JsonElement a, string campo, int maximo)
    { var texto = Campo(a, campo, JsonValueKind.String).GetString(); return !string.IsNullOrWhiteSpace(texto) && texto.Length <= maximo ? texto : throw ErroDoBrainMcp.Entrada(); }
    private static string[] Lista(JsonElement a, string campo)
    {
        if (!a.TryGetProperty(campo, out var lista)) return [];
        if (lista.ValueKind != JsonValueKind.Array || lista.GetArrayLength() > 50) throw ErroDoBrainMcp.Entrada();
        return lista.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 and <= 100 } s ? s : throw ErroDoBrainMcp.Entrada()).ToArray();
    }
    private static T ObrigatorioEnum<T>(JsonElement a, string campo) where T : struct, Enum =>
        Enum.TryParse<T>(Texto(a, campo, 80), true, out var valor) && Enum.IsDefined(valor) ? valor : throw ErroDoBrainMcp.Entrada();
    private static T? OpcionalEnum<T>(JsonElement a, string campo) where T : struct, Enum => a.TryGetProperty(campo, out _) ? ObrigatorioEnum<T>(a, campo) : null;
}
