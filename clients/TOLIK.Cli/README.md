# TOLIK CLI

Тонкий клиент для Linux, Windows и macOS. CLI не содержит Core, память, ключи моделей или Agent permissions: он отправляет запросы в TOLIK Core.

## Запуск

    dotnet run --project clients/TOLIK.Cli -- --help
    dotnet run --project clients/TOLIK.Cli -- modules --endpoint http://127.0.0.1:5080 --token SESSION_TOKEN
    dotnet run --project clients/TOLIK.Cli -- chat "Привет" --endpoint http://127.0.0.1:5080 --token SESSION_TOKEN
    dotnet run --project clients/TOLIK.Cli -- task create --description "Проверить проект" --endpoint http://127.0.0.1:5080 --token SESSION_TOKEN

Токен передаётся через --token или TOLIK_SESSION_TOKEN и не сохраняется CLI. HTTP 401/403 возвращаются как ошибки с кодом завершения 2.
