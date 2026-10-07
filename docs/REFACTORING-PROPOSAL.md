# Перестроить JobFinder вокруг ответственности и проверяемых переходов

**30 сентября 2026 · baseline `cfbefc6` · предложение, не реализация.**

**Рекомендую модульный монолит из пяти функциональных модулей, с одним хостом, одним типом DbContext и PostgreSQL.** Крупный рефакторинг нужен, но его цель — закрепить владельцев правил, контрактов и переходов состояния. Конфигурационные файлы, JSON Schema и существующие сервисы сами по себе не являются плохой архитектурой. Проблема возникает там, где одно решение вручную представлено в нескольких местах, а подготовка, одобрение и отправка не образуют согласованную операцию. Целевая конструкция: типизированный каталог LLM-задач, генерируемые схемы, отдельные runtime-политики и единый жизненный цикл доставки. Начинать следует с корректности Hiring; Wolverine рассматривать следующим этапом, после ограниченного технического испытания.

## Семь дефектов указывают на разрыв ответственности

Основа системы пригодна для развития: Core отделён от EF, очереди и документы сохраняются в PostgreSQL, есть notification outbox, ограничения источников, кеширование GeneratedAnswers и содержательные проверки писем/PDF. Проверки качества уже во многом общие; повторяется их применение и управление состоянием вокруг них ([LetterWriter](../src/JobFinder.Pipeline/Career/LetterWriter.cs#L98), [ResumeTailor](../src/JobFinder.Pipeline/Documents/ResumeTailor.cs#L57), [Notifier](../src/JobFinder.Pipeline/Notifications/Notifier.cs#L8)).

Ниже — подтверждённые заметками свойства кода и выведенные из них сценарии. **Это не воспроизведённые производственные инциденты.** Отчёт синтезирует четыре предоставленные исследовательские записки; новые исследования, тесты, сборки, обращения к БД и чтение приватной конфигурации не проводились.

**Десериализация подменяет проверку схемы.** `CompleteJsonAsync` десериализует ответ и вызывает необязательную семантическую проверку, но локально JSON Schema не исполняет. Поэтому `{}` превращается в `PostExtraction` с `IsVacancy=false`, проходит `Validate` и обрабатывается как «не вакансия». Неполный ответ способен молча исключить полезный пост вместо переключения модели ([клиент](../src/JobFinder.Llm/Client/LlmClient.cs#L40), [контракт](../src/JobFinder.Llm/Tasks/LlmContracts.cs#L11), [обработчик](../src/JobFinder.Pipeline/Workers/RawPostWorker.cs#L115)).

**ExtraBody обходит контракт запроса.** Произвольный JSON сливается последним и может заменить модель, сообщения, схему или `provider.require_parameters`. Некорректный JSON разбирается до защищённого участка transport; исключение не обязано попасть в обычный механизм ротации. Настройка способна отключить гарантии, которые остальной код считает действующими ([порядок выполнения](../src/JobFinder.Llm/Client/OpenRouterTransport.cs#L35), [слияние](../src/JobFinder.Llm/Client/OpenRouterTransport.cs#L131), [перезапись](../src/JobFinder.Llm/Client/OpenRouterTransport.cs#L182)).

**Настройки сохраняются неатомарно.** Страница делает шесть отдельных `SetAsync`, каждый со своим commit. Ошибка оставляет частичное изменение, а устаревшая форма может затереть переключённую ботом паузу: версии редактирования нет. Это нарушение пользовательского намерения даже при единственном пользователе ([форма](../src/JobFinder.App/Components/Pages/SettingsPage.razor#L131), [хранилище](../src/JobFinder.Pipeline/Settings/RuntimeSettings.cs#L66), [сущность](../src/JobFinder.Core/Domain/Entities.cs#L243)).

**Отмена и редактирование проигрывают устаревшему worker.** Worker читает Pending, ожидает reviewer, затем меняет статус по ID без проверки исходного состояния и ревизии. Между этими действиями пользователь может отменить отклик или изменить письмо; старый обработчик способен восстановить Sending либо перезаписать текст ([чтение](../src/JobFinder.Pipeline/Workers/ApplyWorker.cs#L49), [обновление](../src/JobFinder.Pipeline/Workers/ApplyWorker.cs#L325), [отмена](../src/JobFinder.Pipeline/Actions/ApprovalService.cs#L160), [редактирование](../src/JobFinder.Pipeline/Actions/ApprovalService.cs#L247)).

**Неизвестный результат отправки превращается в повтор.** Внешний вызов предшествует сохранению Sent. Сбой после принятия сообщения, но до commit, может вернуть работу в Pending; startup безусловно сбрасывает туда Sending. Следствие — возможный повтор работодателю. Наличие отклика на hh не доказывает отдельно доставку сопроводительного письма ([отправка и завершение](../src/JobFinder.Pipeline/Workers/ApplyWorker.cs#L172), [повтор](../src/JobFinder.Pipeline/Workers/ApplyWorker.cs#L213), [recovery](../src/JobFinder.Pipeline/PipelineServices.cs#L220), [hh](../src/JobFinder.Integrations/HeadHunter/HhApplier.cs#L145)).

**История выбирает артефакт после отправки.** Attachment API возвращает путь и имя, а `FinishAsync` затем запрашивает последнее резюме. Параллельная регенерация допускает запись ID другого PDF. Для hh локальное резюме также может попасть в историю, хотя этот путь его не прикладывал ([attachment](../src/JobFinder.Pipeline/Documents/ResumeTailor.cs#L121), [атрибуция](../src/JobFinder.Pipeline/Workers/ApplyWorker.cs#L243)).

**Recovery выполняется раньше захвата владения сервером.** `DatabaseInitializer` зарегистрирован перед `SingleInstanceGuard`. Второй сервер способен изменить Sending действующего процесса и только затем остановиться на advisory lock. Перестановка этой границы обязательна раньше любого внедрения очередей ([регистрация](../src/JobFinder.Pipeline/PipelineServices.cs#L174), [восстановление](../src/JobFinder.Pipeline/PipelineServices.cs#L216), [guard](../src/JobFinder.Pipeline/PipelineServices.cs#L250)).

Историческая ошибка первого обращения **уже исправлена в baseline**: writer и reviewer используют Application, соответствующие Application/Resume fallback-настройки усилены. Открытая задача — сделать это проверяемым свойством каждой задачи и каждого fallback, а не повторять старую претензию ([выбор reviewer](../src/JobFinder.Llm/Tasks/CareerLlm.cs#L137), [передача first](../src/JobFinder.Pipeline/Career/LetterWriter.cs#L235), [маршруты](../config/appsettings.json#L249)).

## Пять модулей закрепляют владельцев бизнес-действий

Границы сначала вводятся папками и namespace внутри текущих проектов. Один DbContext означает общую модель и миграции, **не общий экземпляр между параллельными операциями**. Отдельные сборки полезны лишь там, где позже потребуется более жёсткая изоляция.

| Модуль | Ответственность |
|---|---|
| Candidate | Профиль, подтверждённые факты, истории, снимки доказательств. |
| Discovery | Источники, посты, дедупликация, вакансии, компании, фильтрация и оценка соответствия. |
| Hiring | Попытки отклика, письма, резюме для откликов, одобрение, контакты, переписка, доставка и воронка. |
| Preparation | Интервью, HR-ответы, тренировки и производные материалы подготовки. |
| Engagement | Аудитория Threads, комментарии, одобрение публикаций и их результаты. |

UI, бот и CLI вызывают одни обычные DI-handlers. Небольшие контракты передают команды, результаты и снимки, без EF entities и `IQueryable`. Межмодульные записи выполняет владелец; аналитическим read-only проекциям допустимы совместные запросы. Координация сценариев не должна создавать обратные зависимости модулей. Двадцать новых проектов и generic repository для каждой сущности здесь не нужны.

ArchUnitNET рекомендован для запрета межмодульных implementation-зависимостей и прямых отправок из UI. Принятие пакета требует короткого spike на используемых .NET/xUnit v3, включая намеренно нарушающий правило async/Razor-пример: совместимость по metadata ещё не доказывает работу анализа ([официальное руководство](https://archunitnet.readthedocs.io/en/latest/guide/)).

## Контракт LLM и runtime-политика получают по одному владельцу

Для deployment остаётся **Microsoft.Extensions.Options**: подключения, endpoints, пути, proxy, telemetry и ссылки на секреты. Нужны единая композиция для сервера/CLI, `IValidateOptions` и startup validation; CLI должен явно вызвать проверку, поскольку построение host не означает его запуск ([Microsoft Options](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options?view=aspnetcore-10.0), [CLI](../src/JobFinder.App/Cli/CliHost.cs#L89)).

Изменяемые пользователем automation/search/LLM-политики становятся несколькими типизированными агрегатами PostgreSQL. Публикация проверяет весь агрегат и expected revision; политика, история изменения и необходимый outbox сохраняются одной транзакцией. Операция закрепляет проверенный snapshot, а устаревшая форма получает конфликт. Реестр моделей, маршруты по ID и профили качества компилируются в готовый план запроса: неизвестная задача или ослабляющий обязательное качество fallback отклоняются. ExtraBody заменяется типизированными настройками и ограниченным extension-объектом без доступа к защищённым полям ([EF concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)).

Это развитие существующего хранения: **`app_documents` с колонкой типа `json` сохраняется**, как и терпимые читатели старого JSON и `{ get; set; }`. Нужна transaction-aware операция compare-and-swap, а не вызов нынешнего unconditional upsert внутри чужой транзакции. Операционные курсоры, cooldown и прогресс постепенно получают свои хранилища. При переносе импортируются действующие значения, а не только публичные defaults; для каждого агрегата остаётся один writer ([документы](../src/JobFinder.Pipeline/Storage/PgStores.cs#L24), [mapping](../src/JobFinder.Data/JobFinderDbContext.cs#L285)).

LLM-задачу описывает `LlmTaskDefinition<TInput,TWireResult>`: ID, prompt/bindings, wire-метаданные, качество, маршрут, обязательные validators и версии обработки. Каталог выводит списки обязательных prompts и task-shaped probes. **Схема генерируется нативным `System.Text.Json.Schema.JsonSchemaExporter` из source-generated `JsonTypeInfo` отдельного wire DTO.** Нельзя экспортировать общий persistence DTO вслепую: так в контракт модели попадут вычисляемые или управляемые приложением поля. Required, nullable, enum и диапазоны задаются явно ([Microsoft exporter](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/extract-schema)).

Приём ответа идёт через захват envelope/usage, локальный schema evaluator, десериализацию и семантическую проверку. Канонический контракт отделяется от версионированной provider projection; упрощение схемы для провайдера не ослабляет локальную проверку. JsonSchema.Net — перспективный evaluator, **но проверка лицензии конкретного бинарного пакета является обязательным условием принятия**: MIT исходников не позволяет назвать все дистрибутивы безусловно MIT ([валидация](https://docs.json-everything.net/schema/basics/), [лицензия исследованного пакета 9.4.0](https://www.nuget.org/packages/JsonSchema.Net/9.4.0/License), [OpenRouter structured outputs](https://openrouter.ai/docs/guides/features/structured-outputs)).

Provenance собирается из того же плана: prompt/schema/provider-projection hashes, правила, snapshot политики, входы и зависимости от фактов. Cache identity вычисляется до генерации; фактическая модель, попытки и стоимость записываются после. Сохраняются selective invalidation и GeneratedAnswers. Microsoft.Extensions.AI допустим позднее как адаптер, сохраняя нынешние rotation, breaker, budget и accounting; Semantic Kernel или agents ради схем не нужны ([Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)).

## Доставка требует явного исхода независимо от очереди

Hiring получает общий `DeliveryGateway` и `EmployerContentGate`, делегирующий существующим LetterWriter/ResumeTailor. `ApprovedDispatch` закрепляет попытку, intent/Fresh start, адресата, канал, точный текст, proof и approval revision. `ApprovedResume` несёт **ID реально выбранного артефакта, SHA-256 и проверенные байты либо неизменяемый файл**. Проверка остаётся при одобрении и перед отправкой; для hh нельзя приписывать локальное вложение без фактической загрузки. Обычные ответы в переписке сохраняют собственные правила.

```text
Подготовка → результаты этапов → проверенный пакет → одобрение ревизии
→ CAS Ready/Revision → Submitting + сохранённый DeliveryIntent
→ Confirmed | KnownNotSent → допустимый retry | OutcomeUnknown → сверка
```

LLM/PDF и сеть выполняются вне SQL-транзакций. Короткий CAS проверяет состояние и ревизию; проигравшая отправка не вызывает адаптер. После claim отмена честно возвращает «уже отправляется». Business operation key предотвращает повтор одной команды даже с новым envelope ID; Fresh start получает новую попытку. Receipt, история и notification фиксируются вместе. **OutcomeUnknown нельзя автоматически повторять ради автоматизации**: при недостаточном доказательстве нужен разбор, а не возврат в Pending. Истечение lease также не доказывает отсутствие внешнего эффекта.

**Wolverine + EF/PostgreSQL — предпочтительный кандидат для durable execution, условно после ограниченного spike.** Его ценность — сохранять результат этапа вместе с командой следующего. Требуется доказать transaction scope с нынешним DbContext factory, package compatibility, crash recovery и producer-only CLI. RabbitMQ, Marten и новый сервис не нужны. Обычные handlers оборачиваются тонкими Wolverine transport adapters; отдельный MediatR-подобный dispatcher одновременно не добавляется ([EF inbox/outbox](https://raw.githubusercontent.com/JasperFx/wolverine/main/docs/guide/durability/efcore/outbox-and-inbox.md), [PostgreSQL](https://raw.githubusercontent.com/JasperFx/wolverine/main/docs/guide/durability/postgresql.md)).

Небольшой календарный слой пока сохраняется. Quartz оправдан при реальной потребности в календарях/misfire, но не заменяет очереди, бизнес-идемпотентность или гарантии доставки ([Quartz](https://www.quartz-scheduler.net/documentation/best-practices.html)). Связку Quartz + Hangfire + Wolverine не вводить. Если spike Wolverine провалится, единственным fallback станет PostgreSQL work table с operation key, due time, lease/CAS и outbox — не вторая параллельная платформа.

## Миграция принимается по поведению при сбоях

Последовательность: целостный участок Hiring dispatch/revision/artifact; затем Wolverine spike и поэтапный перенос исполнения; далее task catalog и политики по функциям, остальные модульные границы. Каждый тип работы переключается на одного владельца. Старый sender исключает мигрированные операции; откат не должен возвращать слепой Sending→Pending. Завершённые дорогие стадии сохраняются отдельно, чтобы restart не требовал повторять весь pipeline.

Приёмка начинается с **изолированного PostgreSQL и fake-внешних эффектов**. Fault injection до commit, после state+command commit и после принятия сообщения внешним адаптером должен доказать атомарность, восстановление и отсутствие автоматического повторного send при неизвестном исходе. Барьеры в тестах воспроизводят cancel/edit во время review, duplicate business operation и замену PDF после выбора. UI, бот и CLI обязаны давать одинаковый результат одного handler.

Отдельные fixtures проверяют пустой JSON, missing/null/enum/extra fields и provider projections; старые persistence payloads сохраняют defaults. Изменение schema, reviewer route или rules инвалидирует нужный snapshot hash, эквивалентная перестановка JSON-полей политики — нет. Конфликт config revision не затирает новое значение. Message/PDF gates сохраняют **порог ≥9, максимальный reasoning для Application/Resume**, полный checklist и отсутствие unchecked fallback. Второй сервер останавливается до recovery; потеря владения прекращает новые эффекты. MTProto остаётся у единственного сервера.

Производительность принимается сравнением одинаковых сценариев **cold/warm/retry**: p50/p95 времени ожидания и подготовки, число LLM-вызовов, стоимость и раунды до принятого результата. До переключения фиксируются baseline и допустимые регрессии; измерения включают неудачные оплаченные ответы. Ускорение нельзя объявлять без замеров или покупать снижением качества. Финальные implementation gates — сборка без warnings и обязательные интеграционные проверки без неожиданных skips; здесь они не запускались.

## Первый PR должен сделать отправку проверяемой

Первый ограниченный PR: queued Hiring от approve/edit/cancel до dispatch, **без внедрения framework**. Он переносит захват владельца перед recovery, добавляет revision/CAS и ключ операции, сохраняет delivery intent с OutcomeUnknown, возвращает из attachment gate фактический ID/hash и удаляет post-send выбор Latest. Существующие adapters и quality algorithms используются через общий gate; тесты моделируют гонки и сбои на изолированной БД.

Результат PR — отменённый или изменённый черновик не уходит по старой ревизии, история идентифицирует отправленный материал, а неопределённый исход виден и не запускает слепой повтор. После этого Wolverine оценивается как сокращение инфраструктурной работы на уже корректной модели. Это предложение последующей реализации; production не изменён.
