# Validação realizada

- Compilação com SDK .NET 10: sem erros e sem avisos.
- Servidor ASP.NET executado em localhost:5080.
- Teste de integração com banco SQLite real: aprovado.
- Verificados: cadastro, autenticação, acesso administrativo, rejeição de estoque insuficiente, carrinho persistente, cálculo do total, criação do pedido, carrinho vazio após compra, bloqueio de confirmação duplicada sequencial, isolamento de pedidos entre clientes, transição inválida de status, cancelamento e reposição única de estoque, validação CSRF.
- Navegador: vitrine, login administrativo, métricas, tabela de produtos, formulário de novo produto e logout verificados.
- Interface mobile: viewport de 390 × 844, sem rolagem horizontal observada.
- Instalação da PWA em telefone físico e publicação HTTPS não foram realizadas.

O arquivo ZIP contém código e assets, sem dados de contas de teste ou chaves de sessão. O banco é criado no primeiro uso.
