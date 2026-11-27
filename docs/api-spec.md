# Спецификация API

JSON передаётся по HTTPS. Для защищённых методов нужен заголовок `Authorization: Bearer <JWT>`. Статусы в API передаются именами `Accepted`, `Layout`, `Approved`, `Printing`, `Ready`, `Issued`, `Cancelled`.

| Метод и путь | Администратор | Менеджер | Печатник | Успех |
|---|---|---|---|---|
| GET /health | Да | Да | Да | 200 |
| POST /api/auth/login | Да | Да | Да | 200, токен и пользователь |
| GET /api/auth/me | Да | Да | Да | 200, роль и учётная запись |
| POST /api/auth/logout | Да | Да | Да | 204 |
| GET /api/orders | Да | Да | Назначенные | 200 |
| GET /api/orders/{id} | Да | Да | Назначенный | 200 |
| POST /api/orders | Да | Да | Запрещено | 201 |
| PUT /api/orders/{id} | Да | Да | Запрещено | 204 |
| POST /api/orders/{id}/transition | Да | Да | «Печать → Готов» для назначенного заказа | 204 |
| GET /api/orders/statistics | Все заказы | Все заказы | Назначенные | 200 |
| GET /api/orders/export/{format} | Да | Да | Запрещено | 200, файл |
| GET /api/customers | Да | Да | Запрещено | 200 |
| POST /api/customers | Да | Да | Запрещено | 201 |
| PUT /api/customers/{id} | Да | Да | Запрещено | 204 |
| POST /api/customers/import | Да | Да | Запрещено | 200, число добавленных записей |
| GET /api/users | Да | Запрещено | Запрещено | 200 |
| GET /api/users/printers | Да | Да | Запрещено | 200 |
| POST /api/users | Да | Запрещено | Запрещено | 201 |
| PUT /api/users/{id}/access | Да | Запрещено | Запрещено | 204 |
| GET /api/debug/requests (Development) | Да | Запрещено | Запрещено | 200 |

`q` и `status` поддерживаются списком и выгрузкой. `version` обязателен при редактировании и переходе. Для импорта отправляется тело `text/csv` в UTF-8. Статус 404 возвращается и для чужого заказа печатника.

Вход по умолчанию ограничен пятью запросами в минуту на IP-адрес. При превышении лимита `POST /api/auth/login` возвращает **429 Too Many Requests**. Повторите вход через минуту.

Пример запроса к `POST /api/orders` с тиражом 0 и существующим заказчиком:

```json
{"customerId":1,"title":"Афиша","specification":"А3, 4+0","quantity":0}
```

Ответ 400 содержит поля Problem Details, в том числе:

```json
{"status":400,"title":"Проверьте данные","detail":"Тираж должен быть от 1 до 1 000 000 экземпляров."}
```

Запросы для ручного воспроизведения находятся в [requests.http](../requests.http).

## Отдельный запуск API для запросов HTTP

В терминале из корня решения укажите отдельный файл БД для ручных запросов. Для HTTPS на Windows требуется доверенный сертификат разработки ASP.NET Core.

```powershell
dotnet dev-certs https --trust
$env:Print__Database = Join-Path $env:TEMP 'printinghouse-http.db'
$env:Print__SeedDemo = 'true'
dotnet run --project .\PrintingHouse.Api --launch-profile 'API (отдельный запуск)'
```

Подставляйте локальные учётные данные и токены в рабочую копию файла запросов. Перед изменением заказа получите его номер и версию из списка. Для сценария печатника выберите назначенный этому сотруднику заказ со статусом Printing.
