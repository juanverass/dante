using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Orquestracao.Migrations
{
    /// <inheritdoc />
    public partial class Delegacoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "delegacoes_por_execucao",
                schema: "orquestracao",
                table: "versoes_de_agente",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "minutos_de_delegacao",
                schema: "orquestracao",
                table: "versoes_de_agente",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "minutos_para_iniciar_delegacao",
                schema: "orquestracao",
                table: "versoes_de_agente",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "paralelismo_de_delegacoes",
                schema: "orquestracao",
                table: "versoes_de_agente",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "pode_consultar",
                schema: "orquestracao",
                table: "versoes_de_agente",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<int>(
                name: "profundidade_maxima_de_delegacao",
                schema: "orquestracao",
                table: "versoes_de_agente",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "id_execucao_pai",
                schema: "orquestracao",
                table: "execucoes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "id_execucao_raiz",
                schema: "orquestracao",
                table: "execucoes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "profundidade",
                schema: "orquestracao",
                table: "execucoes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill (#309): toda execução existente é uma raiz, de profundidade 0, que aponta para si mesma. Os
            // defaults só serviram para criar as colunas em tabela com linhas; daqui em diante o valor vem sempre do
            // domínio (sem default, uma inserção sem raiz falha em vez de apontar para um id inexistente).
            migrationBuilder.Sql("""
                UPDATE orquestracao.execucoes SET id_execucao_raiz = id, profundidade = 0;
                ALTER TABLE orquestracao.execucoes ALTER COLUMN id_execucao_raiz DROP DEFAULT;
                ALTER TABLE orquestracao.execucoes ALTER COLUMN profundidade DROP DEFAULT;
                """);

            migrationBuilder.CreateTable(
                name: "delegacoes",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao_origem = table.Column<Guid>(type: "uuid", nullable: false),
                    id_etapa_origem = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao_subordinada = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao_raiz = table.Column<Guid>(type: "uuid", nullable: false),
                    id_agente_solicitante = table.Column<Guid>(type: "uuid", nullable: false),
                    id_versao_solicitante = table.Column<Guid>(type: "uuid", nullable: false),
                    hash_da_versao_solicitante = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    id_agente_destinatario = table.Column<Guid>(type: "uuid", nullable: false),
                    handle_do_destinatario = table.Column<string>(type: "character varying(31)", maxLength: 31, nullable: false),
                    id_versao_destinataria = table.Column<Guid>(type: "uuid", nullable: false),
                    hash_da_versao_destinataria = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    contrato = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    entrada = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    hash_da_entrada = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    objetivo_redigido = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    objetivo_alterado_pela_redacao = table.Column<bool>(type: "boolean", nullable: false),
                    hash_do_objetivo = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    criterios = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    minutos_para_iniciar = table.Column<int>(type: "integer", nullable: false),
                    minutos_de_duracao = table.Column<int>(type: "integer", nullable: false),
                    escrita = table.Column<bool>(type: "boolean", nullable: false),
                    cadeia_de_agentes = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    chave_de_deduplicacao = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    motivo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    resultado = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    hash_do_resultado = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    id_versao_do_autor_do_resultado = table.Column<Guid>(type: "uuid", nullable: true),
                    id_sessao_do_resultado = table.Column<Guid>(type: "uuid", nullable: true),
                    hash_do_artefato_lido = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    aceita_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    iniciada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    encerrada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delegacoes", x => x.id);
                    table.CheckConstraint("ck_delegacoes_contrato", "contrato IN ('consulta.v1', 'revisao.v1')");
                    table.CheckConstraint("ck_delegacoes_estado", "estado BETWEEN 0 AND 7");
                    table.CheckConstraint("ck_delegacoes_hashes", "hash_da_versao_solicitante ~ '^[0-9a-f]{64}$' AND hash_da_versao_destinataria ~ '^[0-9a-f]{64}$' AND hash_da_entrada ~ '^[0-9a-f]{64}$' AND hash_do_objetivo ~ '^[0-9a-f]{64}$' AND chave_de_deduplicacao ~ '^[0-9a-f]{64}$' AND (hash_do_resultado IS NULL OR hash_do_resultado ~ '^[0-9a-f]{64}$') AND (hash_do_artefato_lido IS NULL OR hash_do_artefato_lido ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("ck_delegacoes_hierarquia", "id_execucao_subordinada <> id_execucao_origem AND id_agente_destinatario <> id_agente_solicitante AND cardinality(cadeia_de_agentes) >= 1 AND cadeia_de_agentes[cardinality(cadeia_de_agentes)] = id_agente_solicitante AND array_position(cadeia_de_agentes, NULL) IS NULL AND NOT (id_agente_destinatario = ANY (cadeia_de_agentes))");
                    table.CheckConstraint("ck_delegacoes_prazos", "minutos_para_iniciar BETWEEN 1 AND 30 AND minutos_de_duracao BETWEEN 1 AND 60");
                    table.CheckConstraint("ck_delegacoes_resultado", "(estado = 3) = (resultado IS NOT NULL) AND (resultado IS NULL) = (hash_do_resultado IS NULL) AND (resultado IS NULL) = (id_versao_do_autor_do_resultado IS NULL) AND (id_versao_do_autor_do_resultado IS NULL OR id_versao_do_autor_do_resultado = id_versao_destinataria) AND (resultado IS NOT NULL OR (id_sessao_do_resultado IS NULL AND hash_do_artefato_lido IS NULL))");
                    table.CheckConstraint("ck_delegacoes_sem_escrita", "escrita = false");
                    table.ForeignKey(
                        name: "FK_delegacoes_agentes_id_agente_destinatario",
                        column: x => x.id_agente_destinatario,
                        principalSchema: "orquestracao",
                        principalTable: "agentes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delegacoes_agentes_id_agente_solicitante",
                        column: x => x.id_agente_solicitante,
                        principalSchema: "orquestracao",
                        principalTable: "agentes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delegacoes_etapas_da_execucao_id_etapa_origem_id_execucao_o~",
                        columns: x => new { x.id_etapa_origem, x.id_execucao_origem },
                        principalSchema: "orquestracao",
                        principalTable: "etapas_da_execucao",
                        principalColumns: new[] { "id", "id_execucao" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delegacoes_execucoes_id_execucao_origem",
                        column: x => x.id_execucao_origem,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delegacoes_execucoes_id_execucao_raiz",
                        column: x => x.id_execucao_raiz,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delegacoes_execucoes_id_execucao_subordinada",
                        column: x => x.id_execucao_subordinada,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delegacoes_sessoes_de_agente_id_sessao_do_resultado",
                        column: x => x.id_sessao_do_resultado,
                        principalSchema: "orquestracao",
                        principalTable: "sessoes_de_agente",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delegacoes_versoes_de_agente_id_versao_destinataria",
                        column: x => x.id_versao_destinataria,
                        principalSchema: "orquestracao",
                        principalTable: "versoes_de_agente",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delegacoes_versoes_de_agente_id_versao_do_autor_do_resultado",
                        column: x => x.id_versao_do_autor_do_resultado,
                        principalSchema: "orquestracao",
                        principalTable: "versoes_de_agente",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delegacoes_versoes_de_agente_id_versao_solicitante",
                        column: x => x.id_versao_solicitante,
                        principalSchema: "orquestracao",
                        principalTable: "versoes_de_agente",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_versoes_de_agente_consulta_com_coordenacao",
                schema: "orquestracao",
                table: "versoes_de_agente",
                sql: "cardinality(pode_consultar) = 0 OR 'Coordenacao' = ANY (ferramentas)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_versoes_de_agente_limites_de_delegacao",
                schema: "orquestracao",
                table: "versoes_de_agente",
                sql: "(profundidade_maxima_de_delegacao IS NULL AND delegacoes_por_execucao IS NULL AND minutos_para_iniciar_delegacao IS NULL AND minutos_de_delegacao IS NULL AND paralelismo_de_delegacoes IS NULL) OR (profundidade_maxima_de_delegacao IS NOT NULL AND delegacoes_por_execucao IS NOT NULL AND minutos_para_iniciar_delegacao IS NOT NULL AND minutos_de_delegacao IS NOT NULL AND paralelismo_de_delegacoes IS NOT NULL AND profundidade_maxima_de_delegacao BETWEEN 1 AND 1 AND delegacoes_por_execucao BETWEEN 1 AND 4 AND minutos_para_iniciar_delegacao BETWEEN 1 AND 30 AND minutos_de_delegacao BETWEEN 1 AND 60 AND paralelismo_de_delegacoes BETWEEN 1 AND 1)");

            migrationBuilder.CreateIndex(
                name: "IX_execucoes_id_execucao_pai",
                schema: "orquestracao",
                table: "execucoes",
                column: "id_execucao_pai");

            migrationBuilder.CreateIndex(
                name: "IX_execucoes_id_execucao_raiz",
                schema: "orquestracao",
                table: "execucoes",
                column: "id_execucao_raiz");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execucoes_delegada",
                schema: "orquestracao",
                table: "execucoes",
                sql: "(tipo = 3) = (id_execucao_pai IS NOT NULL) AND (tipo <> 3 OR id_versao_do_agente IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execucoes_hierarquia",
                schema: "orquestracao",
                table: "execucoes",
                sql: "(id_execucao_pai IS NULL) = (profundidade = 0) AND (profundidade = 0) = (id_execucao_raiz = id) AND profundidade BETWEEN 0 AND 1");

            migrationBuilder.CreateIndex(
                name: "IX_delegacoes_id_agente_destinatario",
                schema: "orquestracao",
                table: "delegacoes",
                column: "id_agente_destinatario");

            migrationBuilder.CreateIndex(
                name: "IX_delegacoes_id_agente_solicitante",
                schema: "orquestracao",
                table: "delegacoes",
                column: "id_agente_solicitante");

            migrationBuilder.CreateIndex(
                name: "IX_delegacoes_id_etapa_origem_id_execucao_origem",
                schema: "orquestracao",
                table: "delegacoes",
                columns: new[] { "id_etapa_origem", "id_execucao_origem" });

            migrationBuilder.CreateIndex(
                name: "IX_delegacoes_id_execucao_origem",
                schema: "orquestracao",
                table: "delegacoes",
                column: "id_execucao_origem");

            migrationBuilder.CreateIndex(
                name: "IX_delegacoes_id_execucao_raiz",
                schema: "orquestracao",
                table: "delegacoes",
                column: "id_execucao_raiz");

            migrationBuilder.CreateIndex(
                name: "IX_delegacoes_id_execucao_subordinada",
                schema: "orquestracao",
                table: "delegacoes",
                column: "id_execucao_subordinada",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delegacoes_id_sessao_do_resultado",
                schema: "orquestracao",
                table: "delegacoes",
                column: "id_sessao_do_resultado");

            migrationBuilder.CreateIndex(
                name: "IX_delegacoes_id_versao_destinataria",
                schema: "orquestracao",
                table: "delegacoes",
                column: "id_versao_destinataria");

            migrationBuilder.CreateIndex(
                name: "IX_delegacoes_id_versao_do_autor_do_resultado",
                schema: "orquestracao",
                table: "delegacoes",
                column: "id_versao_do_autor_do_resultado");

            migrationBuilder.CreateIndex(
                name: "IX_delegacoes_id_versao_solicitante",
                schema: "orquestracao",
                table: "delegacoes",
                column: "id_versao_solicitante");

            migrationBuilder.CreateIndex(
                name: "ix_delegacoes_unidade_nao_terminal",
                schema: "orquestracao",
                table: "delegacoes",
                columns: new[] { "id_execucao_raiz", "chave_de_deduplicacao" },
                unique: true,
                filter: "estado IN (0, 1, 2)");

            migrationBuilder.AddForeignKey(
                name: "FK_execucoes_execucoes_id_execucao_pai",
                schema: "orquestracao",
                table: "execucoes",
                column: "id_execucao_pai",
                principalSchema: "orquestracao",
                principalTable: "execucoes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // A raiz aponta para uma execução existente (a própria, numa raiz). Fora do modelo do EF de propósito: uma
            // FK de uma linha para ela mesma faria o EF ver um ciclo ao inserir uma raiz.
            migrationBuilder.Sql("""
                ALTER TABLE orquestracao.execucoes ADD CONSTRAINT fk_execucoes_raiz
                    FOREIGN KEY (id_execucao_raiz) REFERENCES orquestracao.execucoes (id) ON DELETE RESTRICT;
                """);

            // Hierarquia das execuções (AD-70): a delegada é filha de uma execução existente, com a raiz e a autoridade
            // (canal e pessoa) dela e a profundidade dela mais um. Depois de criada, nada disso muda (nem o tipo), então
            // um ciclo na árvore é impossível: o pai sempre existe antes do filho e a ligação é fixa.
            migrationBuilder.Sql("""
                CREATE FUNCTION orquestracao.conferir_hierarquia_da_execucao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    pai orquestracao.execucoes%ROWTYPE;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF NEW.id_execucao_pai IS DISTINCT FROM OLD.id_execucao_pai
                           OR NEW.id_execucao_raiz <> OLD.id_execucao_raiz OR NEW.profundidade <> OLD.profundidade
                           OR NEW.tipo <> OLD.tipo OR NEW.canal <> OLD.canal
                           OR NEW.id_do_usuario_no_canal <> OLD.id_do_usuario_no_canal THEN
                            RAISE EXCEPTION 'hierarquia, tipo e autoridade da execução são imutáveis'
                                USING ERRCODE = 'restrict_violation';
                        END IF;
                        RETURN NEW;
                    END IF;
                    -- Raiz: quem grava sem as colunas novas (SQL escrito para o schema anterior) continua gravando uma raiz
                    -- válida; os checks conferem o resultado.
                    IF NEW.id_execucao_pai IS NULL THEN
                        NEW.id_execucao_raiz := COALESCE(NEW.id_execucao_raiz, NEW.id);
                        NEW.profundidade := COALESCE(NEW.profundidade, 0);
                        RETURN NEW;
                    END IF;
                    SELECT * INTO pai FROM orquestracao.execucoes WHERE id = NEW.id_execucao_pai;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'execução pai inexistente' USING ERRCODE = 'foreign_key_violation';
                    END IF;
                    IF NEW.id_execucao_raiz <> pai.id_execucao_raiz OR NEW.profundidade <> pai.profundidade + 1
                       OR NEW.canal <> pai.canal OR NEW.id_do_usuario_no_canal <> pai.id_do_usuario_no_canal THEN
                        RAISE EXCEPTION 'execução delegada fora da hierarquia ou da autoridade do pai'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_execucoes_hierarquia BEFORE INSERT OR UPDATE ON orquestracao.execucoes
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.conferir_hierarquia_da_execucao();
                """);

            // Delegação (AD-70): na inserção, nasce Solicitada, com a subordinada Delegada filha da origem na mesma raiz,
            // a versão destinatária como responsável da subordinada, a solicitante como responsável da origem, as versões
            // dos agentes declarados com o hash gravado e a cadeia do tamanho da profundidade. Depois: identidade,
            // contrato, entrada, objetivo, limites e cadeia imutáveis; só transições válidas; nada muda depois do estado
            // terminal (o resultado, que só existe na resposta, fica imutável); nenhuma exclusão.
            migrationBuilder.Sql("""
                CREATE FUNCTION orquestracao.conferir_delegacao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    subordinada orquestracao.execucoes%ROWTYPE;
                    origem orquestracao.execucoes%ROWTYPE;
                BEGIN
                    IF NEW.estado <> 0 OR NEW.motivo IS NOT NULL OR NEW.aceita_em IS NOT NULL OR NEW.iniciada_em IS NOT NULL
                       OR NEW.encerrada_em IS NOT NULL THEN
                        RAISE EXCEPTION 'delegação nasce solicitada' USING ERRCODE = 'check_violation';
                    END IF;
                    SELECT * INTO subordinada FROM orquestracao.execucoes WHERE id = NEW.id_execucao_subordinada;
                    SELECT * INTO origem FROM orquestracao.execucoes WHERE id = NEW.id_execucao_origem;
                    IF subordinada.id IS NULL OR origem.id IS NULL THEN
                        RAISE EXCEPTION 'execução da delegação inexistente' USING ERRCODE = 'foreign_key_violation';
                    END IF;
                    IF subordinada.tipo <> 3 OR subordinada.id_execucao_pai <> origem.id
                       OR subordinada.id_execucao_raiz <> NEW.id_execucao_raiz
                       OR subordinada.id_versao_do_agente IS DISTINCT FROM NEW.id_versao_destinataria
                       OR origem.id_versao_do_agente IS DISTINCT FROM NEW.id_versao_solicitante
                       OR cardinality(NEW.cadeia_de_agentes) <> subordinada.profundidade THEN
                        RAISE EXCEPTION 'delegação fora da hierarquia da execução subordinada' USING ERRCODE = 'check_violation';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM orquestracao.versoes_de_agente v WHERE v.id = NEW.id_versao_solicitante
                                   AND v.id_agente = NEW.id_agente_solicitante
                                   AND v.hash_da_configuracao = NEW.hash_da_versao_solicitante)
                       OR NOT EXISTS (SELECT 1 FROM orquestracao.versoes_de_agente v WHERE v.id = NEW.id_versao_destinataria
                                   AND v.id_agente = NEW.id_agente_destinatario
                                   AND v.hash_da_configuracao = NEW.hash_da_versao_destinataria) THEN
                        RAISE EXCEPTION 'versões da delegação não conferem' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_delegacoes_conferidas BEFORE INSERT ON orquestracao.delegacoes
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.conferir_delegacao();

                CREATE FUNCTION orquestracao.proteger_delegacao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'delegações não são removidas' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.estado IN (3, 4, 5, 6, 7) THEN
                        RAISE EXCEPTION 'delegação encerrada é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.id, NEW.id_execucao_origem, NEW.id_etapa_origem, NEW.id_execucao_subordinada, NEW.id_execucao_raiz,
                        NEW.id_agente_solicitante, NEW.id_versao_solicitante, NEW.hash_da_versao_solicitante,
                        NEW.id_agente_destinatario, NEW.handle_do_destinatario, NEW.id_versao_destinataria,
                        NEW.hash_da_versao_destinataria, NEW.contrato, NEW.entrada, NEW.hash_da_entrada, NEW.objetivo_redigido,
                        NEW.objetivo_alterado_pela_redacao, NEW.hash_do_objetivo, NEW.criterios, NEW.minutos_para_iniciar,
                        NEW.minutos_de_duracao, NEW.escrita, NEW.cadeia_de_agentes, NEW.chave_de_deduplicacao, NEW.criada_em)
                       IS DISTINCT FROM
                       (OLD.id, OLD.id_execucao_origem, OLD.id_etapa_origem, OLD.id_execucao_subordinada, OLD.id_execucao_raiz,
                        OLD.id_agente_solicitante, OLD.id_versao_solicitante, OLD.hash_da_versao_solicitante,
                        OLD.id_agente_destinatario, OLD.handle_do_destinatario, OLD.id_versao_destinataria,
                        OLD.hash_da_versao_destinataria, OLD.contrato, OLD.entrada, OLD.hash_da_entrada, OLD.objetivo_redigido,
                        OLD.objetivo_alterado_pela_redacao, OLD.hash_do_objetivo, OLD.criterios, OLD.minutos_para_iniciar,
                        OLD.minutos_de_duracao, OLD.escrita, OLD.cadeia_de_agentes, OLD.chave_de_deduplicacao, OLD.criada_em) THEN
                        RAISE EXCEPTION 'identidade e pedido da delegação são imutáveis' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.estado <> OLD.estado AND NOT (
                           (OLD.estado = 0 AND NEW.estado IN (1, 7, 6)) OR
                           (OLD.estado = 1 AND NEW.estado IN (2, 4, 5, 6)) OR
                           (OLD.estado = 2 AND NEW.estado IN (3, 4, 5, 6))) THEN
                        RAISE EXCEPTION 'transição de delegação inválida' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_delegacoes_protegidas BEFORE UPDATE OR DELETE ON orquestracao.delegacoes
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.proteger_delegacao();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_delegacoes_protegidas ON orquestracao.delegacoes;
                DROP TRIGGER IF EXISTS trg_delegacoes_conferidas ON orquestracao.delegacoes;
                DROP FUNCTION IF EXISTS orquestracao.proteger_delegacao();
                DROP FUNCTION IF EXISTS orquestracao.conferir_delegacao();
                DROP TRIGGER IF EXISTS trg_execucoes_hierarquia ON orquestracao.execucoes;
                DROP FUNCTION IF EXISTS orquestracao.conferir_hierarquia_da_execucao();
                ALTER TABLE orquestracao.execucoes DROP CONSTRAINT IF EXISTS fk_execucoes_raiz;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_execucoes_execucoes_id_execucao_pai",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropTable(
                name: "delegacoes",
                schema: "orquestracao");

            migrationBuilder.DropCheckConstraint(
                name: "ck_versoes_de_agente_consulta_com_coordenacao",
                schema: "orquestracao",
                table: "versoes_de_agente");

            migrationBuilder.DropCheckConstraint(
                name: "ck_versoes_de_agente_limites_de_delegacao",
                schema: "orquestracao",
                table: "versoes_de_agente");

            migrationBuilder.DropIndex(
                name: "IX_execucoes_id_execucao_pai",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropIndex(
                name: "IX_execucoes_id_execucao_raiz",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execucoes_delegada",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execucoes_hierarquia",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropColumn(
                name: "delegacoes_por_execucao",
                schema: "orquestracao",
                table: "versoes_de_agente");

            migrationBuilder.DropColumn(
                name: "minutos_de_delegacao",
                schema: "orquestracao",
                table: "versoes_de_agente");

            migrationBuilder.DropColumn(
                name: "minutos_para_iniciar_delegacao",
                schema: "orquestracao",
                table: "versoes_de_agente");

            migrationBuilder.DropColumn(
                name: "paralelismo_de_delegacoes",
                schema: "orquestracao",
                table: "versoes_de_agente");

            migrationBuilder.DropColumn(
                name: "pode_consultar",
                schema: "orquestracao",
                table: "versoes_de_agente");

            migrationBuilder.DropColumn(
                name: "profundidade_maxima_de_delegacao",
                schema: "orquestracao",
                table: "versoes_de_agente");

            migrationBuilder.DropColumn(
                name: "id_execucao_pai",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropColumn(
                name: "id_execucao_raiz",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropColumn(
                name: "profundidade",
                schema: "orquestracao",
                table: "execucoes");
        }
    }
}
