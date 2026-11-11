# dayz-stats-api

API em ASP.NET Core que importa os logs `.ADM` de um servidor de DayZ para um banco SQLite e expõe as estatísticas: jogadores, tempo online, mortes, kills e rankings.

É a continuação do [dayz-log-analyzer](https://github.com/edudiasr/dayz-log-analyzer), que faz o resumo no terminal. Aqui os dados ficam guardados e dá pra consultar por HTTP: um bot do Discord, um site com ranking ou um painel de admin podem usar a API sem precisar ler log nenhum.

## Endpoints

| Método | Rota | O que faz |
|---|---|---|
| GET | `/api/stats` | resumo do servidor: jogadores, mortes, kills PvP e da IA, horas jogadas, causas de morte |
| GET | `/api/players?search=&page=&pageSize=` | lista de jogadores com busca por nome |
| GET | `/api/players/{id}` | kills, mortes, K/D, sessões e últimas mortes de um jogador |
| GET | `/api/leaderboard?by=kills\|deaths\|playtime&top=` | ranking |
| GET | `/api/deaths?cause=&playerId=&from=&to=&page=` | mortes com filtros |
| POST | `/api/import` | importa os `.ADM` novos da pasta configurada |
| POST | `/api/import/file` | importa um `.ADM` enviado por upload (`multipart/form-data`, campo `file`) |
| GET | `/health` | pra saber se a API está de pé |

A documentação interativa fica em `/scalar` (gerada a partir do OpenAPI em `/openapi/v1.json`).

Exemplo, com o log da pasta `examples` (nomes e ids inventados):

```
GET /api/leaderboard?by=playtime&top=3

[
  {"rank":1,"playerId":"LoBo7xQ2...","name":"Lobo","value":1.55},
  {"rank":2,"playerId":"cRv1Lm...","name":"Corvo","value":0.57},
  {"rank":3,"playerId":"aX9kP3...","name":"Survivor","value":0.52}
]
```

```
GET /api/deaths?cause=jogador

{
  "items": [
    {"id":3,"time":"2026-11-09T00:00:41","playerName":"Survivor","cause":"jogador",
     "killerName":"Lobo","weapon":"Mosin9130","distance":462.3, ...},
    {"id":1,"time":"2026-11-08T23:10:35","playerName":"Raposa","cause":"jogador",
     "killerName":"Lobo","weapon":"AKM","distance":134.7, ...}
  ],
  "page":1, "pageSize":20, "total":2
}
```

Parâmetro inválido (`?by=dano`, `?page=0`, `pageSize` acima de 100) volta 400 no formato ProblemDetails, e jogador que não existe volta 404.

## Rodando

Precisa do .NET 10 SDK.

```
dotnet run --project src/DayzStats.Api
```

Configuração no `appsettings.json` ou por variável de ambiente:

| Chave | Variável | Pra que serve |
|---|---|---|
| `ConnectionStrings:Stats` | `ConnectionStrings__Stats` | arquivo do SQLite (padrão `dayzstats.db`) |
| `Import:Folder` | `Import__Folder` | pasta `profiles` do servidor |
| `Import:ApiKey` | `Import__ApiKey` | se preenchida, os POST de importação exigem o header `X-Api-Key` |

Depois é só chamar `POST /api/import` (pode ser num agendador, a cada restart do servidor). Arquivo que já foi importado é pulado, então chamar de novo não duplica nada.

## Testes

```
dotnet test --solution DayzStats.slnx
```

Tem dois tipos:

- **parser** (`AdmParserTests`): casos tirados de logs reais, como a mesma morte escrita em três linhas, morte pela IA, queda, jogador que reconecta com outro nome, virada da meia-noite e restart.
- **API** (`ApiTests`): sobe a aplicação inteira com `WebApplicationFactory` e um SQLite em memória, importa o log de exemplo e testa os endpoints por HTTP, incluindo a chave de API, os 400 e os 404.

Rodam no GitHub Actions a cada push.

## Notas

As regras de leitura do log são as mesmas do dayz-log-analyzer (jogador pelo id, a mesma morte escrita em várias linhas contando uma vez só). Importei os mesmos 5 dias de log do meu servidor nos dois e os números bateram: 12 jogadores, 10 mortes, 72h26 jogadas. A importação leva por volta de 1 segundo.

Cada arquivo importado tem o nome salvo com índice único e entra numa transação, então pode chamar o `POST /api/import` quantas vezes quiser que não duplica nada.

Coisas que me pegaram no caminho:

- três consultas davam erro 500 porque eu fazia o `Where`/`OrderBy` depois de montar o DTO, e o EF Core não consegue traduzir isso pra SQL. Agora filtra e pagina a entidade primeiro e monta o DTO no final;
- no Windows em português, `double.Parse("41.09")` lê o ponto como separador de milhar, por isso as distâncias usam `CultureInfo.InvariantCulture`;
- o NuGet tentou instalar os pacotes da versão 11, que não rodam no .NET 10, tive que fixar na 10.0.15;
- o .NET 10 mudou o executor do `dotnet test`, por isso o `global.json` tem o `"runner": "Microsoft.Testing.Platform"`.

Usei `EnsureCreated` e não migrations porque o banco é local e o esquema é pequeno. Se crescer eu troco.

## Estrutura

```
src/DayzStats.Api/
  Parsing/AdmParser.cs        leitura do .ADM (regex gerada em tempo de compilação)
  Data/                       entidades e DbContext (EF Core + SQLite)
  Import/LogImporter.cs       grava no banco, sem importar o mesmo arquivo duas vezes
  Endpoints/                  rotas, DTOs e validação
  Program.cs
tests/DayzStats.Api.Tests/    xUnit v3
examples/                     log de exemplo
```
