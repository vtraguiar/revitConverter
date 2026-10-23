# Verificador e conversor de arquivos do Revit

Programa para Windows 10/11 (não precisa instalar nem ter o Revit).

## Usar
Copie a pasta `bin/Release/net48` inteira (o `.exe` precisa da subpasta `nuvem`) e abra `VerificadorRevit.exe`.

- **Abrir / arrastar** `.rvt` ou `.rfa`: mostra a versão, o build, o idioma e a miniatura. Tudo local, nada é enviado.
- **Converter para Revit 2025** (só arquivos do 2026): o arquivo vai para a nuvem da Autodesk, é reconstruído no
  Revit 2025 e volta como `<nome>_R2025.rvt` + relatório do que foi e do que não foi recriado.
- **Exportar IFC (universal)**: gera um `.ifc` pela nuvem da Autodesk.

## Chaves da Autodesk (uma vez)
1. Entre em https://aps.autodesk.com/myapps com uma conta Autodesk e crie um aplicativo do tipo **Server-to-Server**,
   com acesso a *Automation API*, *Model Derivative API* e *Data Management API*.
2. No programa, clique em **Chaves da Autodesk...** e cole o *Client ID* e o *Client Secret*.
   Elas ficam criptografadas para o seu usuário do Windows em `%AppData%\VerificadorRevit\chaves.dat`.

Custo: o Revit Automation tem 300 minutos grátis por mês; acima disso, US$ 3 a cada 12 minutos (tabela da Autodesk).
Os arquivos enviados são apagados pela Autodesk após 24 h.

## Atualizações
Ao abrir, o programa consulta a última versão publicada em
https://github.com/vtraguiar/revitConverter/releases e oferece atualizar (também em Arquivo > Verificar atualizações).
Só instala se o `.zip` conferir com o `.sha256` publicado junto.

Para publicar uma versão nova (na raiz do repositório, depois do commit):
```
./publicar.sh 1.1 "O que mudou"
```
