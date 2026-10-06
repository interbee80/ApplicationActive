# ApplicationActive — REST API (контракт v1)

Все ответы — `application/json; charset=utf-8`, имена полей в `camelCase`.
Время — ISO-8601 UTC строка вида `2025-01-30T09:15:00.000Z`. Даты (`date`, `dayKey`) — локальная дата сервера `yyyy-MM-dd`.
Длительности — секунды (`number`, допускается дробная часть).

## Аутентификация

Заголовок `X-AppActive-Key: <ключ>`.

| Группа методов | Требование |
|---|---|
| `GET /api/health`, `GET /api/version` | без ключа |
| Чтение (`GET /api/apps`, `/api/dashboard`, `/api/groups`, `/api/critical-rules`, `/api/workflow/*`, `/api/sessions`, `/api/export/csv`) | без ключа, если `settings.requiresAdminKey == false` |
| Изменение (`PUT/POST/DELETE` по настройкам, приложениям, группам, правилам) | ключ обязателен, если `settings.requiresAdminKey == true` (иначе разрешено только с loopback) |
| `POST /api/ingest/report`, `GET /api/ingest/config` | всегда требуется ключ агента (ingest key) |
| `POST /api/processes/kill` | как изменение: ключ администратора или loopback |

`GET /api/settings` отдаёт флаг `requiresAdminKey`, чтобы Web-интерфейс знал, нужно ли спрашивать ключ.

---

## Период наблюдения (`from`, `to`)

Методы `/api/dashboard`, `/api/apps`, `/api/apps/{id}/intervals`, `/api/export/csv` принимают период
в параметрах `from` и `to`. Если оба пусты — считаются сутки из `date` (или сегодняшние).

Поддерживаются форматы (как в Grafana):

| Пример | Значение |
|---|---|
| `now` | текущее время |
| `now-1h`, `now-30m`, `now-7d` | сдвиг назад (единицы: `s`, `m`, `h`, `d`, `w`, `M`, `y`) |
| `now+1h` | сдвиг вперёд |
| `now-1h30m` | составная длительность |
| `today`, `yesterday` | начало текущих / конец прошлых суток |
| `2026-10-06` | в `from` — начало суток, в `to` — конец суток |
| `2026-10-06T10:30`, `2026-10-06 10:30` | локальное время; в `to` указанная минута включается целиком |
| `2026-10-06T10:30:00Z` | абсолютное время с зоной |

Ограничения: конец должен быть позже начала, длина не больше 366 суток — иначе `400` с текстом ошибки.

Столбцы графика выбираются автоматически: до 48 часов — по часам (`bucketKind: "hour"`), дольше — по суткам
(`bucketKind: "day"`). Если период выходит за одни сутки, поле `hours` пустое, а данные лежат в `buckets`.

`GET /api/range/resolve?from=now-1d&to=now` разбирает выражение, не обращаясь к базе:

```json
{ "ok": true, "fromUtc": "2025-01-29T13:28:14.000Z", "toUtc": "2025-01-30T13:28:14.000Z",
  "fromLocal": "2025-01-29 13:28:14", "toLocal": "2025-01-30 13:28:14",
  "label": "29.01.2025 13:28 — 30.01.2025 13:28", "totalSeconds": 86400, "bucketKind": "hour" }
```

---

## Служебные

### `GET /api/health`
```json
{
  "status": "ok",
  "version": "1.0.0",
  "startedUtc": "2025-01-30T07:00:00.000Z",
  "uptimeSeconds": 3600.0,
  "databasePath": "C:\\ProgramData\\AppActive\\data\\activity.db",
  "databaseSizeBytes": 409600,
  "monitorEnabled": true,
  "lastIngestUtc": "2025-01-30T09:14:55.000Z",
  "agents": [
    { "sessionId": 1, "userName": "user", "domain": "PC", "pid": 1234, "startedUtc": "2025-01-30T07:05:00.000Z",
      "lastReportUtc": "2025-01-30T09:14:55.000Z", "alive": true, "reportsTotal": 1200,
      "cpuPercent": 0.01, "workingSetMb": 24.5, "agentVersion": "1.0.0", "errors": [] }
  ]
}
```

### `GET /api/version`
```json
{ "version": "1.0.0", "databaseSchemaVersion": 1, "framework": ".NET 8.0" }
```

### `GET /api/diagnostics`
```json
{
  "service": { "cpuPercent": 0.12, "workingSetMb": 96.3, "threadCount": 24, "uptimeSeconds": 3600.0, "gcHeapMb": 12.4, "handleCount": 320 },
  "ingest": { "reportsTotal": 4820, "samplesTotal": 15230, "errorsTotal": 0, "lastReportUtc": "2025-01-30T09:14:55.000Z", "rejectedTotal": 0 },
  "database": { "sizeBytes": 409600, "intervalsToday": 320, "applicationsTotal": 47, "walSizeBytes": 0 },
  "agents": [ { "sessionId": 1, "pid": 1234, "userName": "user", "alive": true, "cpuPercent": 0.01, "workingSetMb": 24.5, "reportsTotal": 1200, "errorsTotal": 0 } ]
}
```

### `GET /api/settings`
```json
{
  "pollIntervalSeconds": 5,
  "activityIdleSeconds": 60,
  "flushSeconds": 15,
  "retentionDays": 90,
  "monitorEnabled": true,
  "collectWindowTitles": true,
  "ignoreProcesses": ["logonui.exe", "lockapp.exe"],
  "agentHeartbeatSeconds": 60,
  "requiresAdminKey": false,
  "updatedUtc": "2025-01-30T07:00:00.000Z"
}
```

### `PUT /api/settings`
Тело — любой набор полей из ответа `GET /api/settings` (кроме `requiresAdminKey`, `updatedUtc`).
Диапазоны: `pollIntervalSeconds` 1..3600, `activityIdleSeconds` 1..86400, `flushSeconds` 1..600, `retentionDays` 1..3650.
Ответ — обновлённый объект настроек. Изменения доезжают до агентов в течение одного цикла опроса.

---

## Приложения

### `GET /api/apps?date=YYYY-MM-DD&search=chrome&sort=todaySeconds|name|lastSeen`
```json
{
  "date": "2025-01-30",
  "trackedSeconds": 18000.0,
  "apps": [
    {
      "id": 3, "exeName": "chrome.exe", "exePath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
      "friendlyName": "Chrome", "displayName": "Chrome", "category": null, "isIgnored": false,
      "firstSeenUtc": "2025-01-01T08:00:00.000Z", "lastSeenUtc": "2025-01-30T09:10:00.000Z",
      "todaySeconds": 3600.0, "percentOfTracked": 20.0, "sessionCount": 1, "userNames": ["user"],
      "groupIds": [1], "groupNames": ["Браузеры"],
      "critical": { "ruleId": 1, "ruleName": "Chrome 2 часа", "limitSeconds": 7200, "usedSeconds": 3600, "percent": 50.0, "exceeded": false, "severity": "warning" }
    }
  ]
}
```
`critical` — `null`, если на приложение (или его группу) нет правила. Если правил несколько — берётся наиболее «жёсткое» (максимальный `percent`).

### `PUT /api/apps/{id}`
Тело: `{ "friendlyName": "Chrome", "category": "Работа", "isIgnored": false }` (все поля необязательны).
Ответ — объект приложения в том же виде, что элемент `apps[]`.

### `GET /api/apps/{id}/intervals?date=YYYY-MM-DD`
```json
{
  "appId": 3, "date": "2025-01-30", "totalSeconds": 3600.0,
  "intervals": [
    { "id": 120, "startUtc": "2025-01-30T09:00:00.000Z", "endUtc": "2025-01-30T09:05:00.000Z",
      "seconds": 300.0, "hourIndex": 9, "sessionId": 1, "userName": "user", "dayKey": "2025-01-30" }
  ]
}
```

---

## Группы приложений

### `GET /api/groups`
```json
{
  "groups": [
    {
      "id": 1, "name": "Браузеры", "description": "Интернет", "color": "#4f8cff",
      "appIds": [3, 7], "appCount": 2, "todaySeconds": 5400.0,
      "critical": { "ruleId": 2, "ruleName": "Браузеры 3 часа", "limitSeconds": 10800, "usedSeconds": 5400, "percent": 50.0, "exceeded": false, "severity": "warning" },
      "apps": [ { "id": 3, "exeName": "chrome.exe", "displayName": "Chrome", "todaySeconds": 3600.0 } ]
    }
  ]
}
```

### `POST /api/groups`
Тело: `{ "name": "Браузеры", "description": "Интернет", "color": "#4f8cff", "appIds": [3, 7] }` → созданная группа.
### `PUT /api/groups/{id}` → обновлённая группа (те же поля, `appIds` заменяет состав).
### `DELETE /api/groups/{id}` → `{ "ok": true }`

---

## Критическое время

### `GET /api/critical-rules`
```json
{
  "rules": [
    { "id": 1, "name": "Chrome 2 часа", "targetType": "app", "targetId": 3, "targetName": "Chrome (chrome.exe)",
      "dailyLimitSeconds": 7200, "enabled": true, "severity": "critical", "daysOfWeek": [1,2,3,4,5],
      "createdUtc": "2025-01-01T08:00:00.000Z", "updatedUtc": "2025-01-01T08:00:00.000Z" }
  ]
}
```
`targetType`: `"app"` | `"group"`. `daysOfWeek`: `1`=понедельник … `7`=воскресенье; `null` или `[]` — каждый день.
`severity`: `"info"` | `"warning"` | `"critical"`.

### `POST /api/critical-rules`
Тело: `{ "name": "...", "targetType": "app", "targetId": 3, "dailyLimitSeconds": 7200, "enabled": true, "severity": "warning", "daysOfWeek": [1,2,3,4,5] }` → правило.
### `PUT /api/critical-rules/{id}` → правило. ### `DELETE /api/critical-rules/{id}` → `{ "ok": true }`

### `GET /api/critical/status?date=YYYY-MM-DD`
```json
{
  "date": "2025-01-30",
  "rules": [
    { "ruleId": 1, "ruleName": "Chrome 2 часа", "targetType": "app", "targetId": 3, "targetName": "Chrome (chrome.exe)",
      "limitSeconds": 7200, "usedSeconds": 7500.0, "percent": 104.2, "exceeded": true, "severity": "critical",
      "enabled": true, "applicableToday": true, "firstExceededUtc": "2025-01-30T14:20:00.000Z" }
  ]
}
```

---

## Workflow (правила реакции)

### `GET /api/workflow/rules`
```json
{
  "rules": [
    { "id": 1, "name": "Алерт по превышению Chrome", "enabled": true, "triggerType": "criticalExceeded",
      "criticalRuleId": 1, "scheduleMinutes": 5, "targetType": null, "targetId": null, "thresholdSeconds": null,
      "actionType": "event", "actionTarget": null, "cooldownMinutes": 60,
      "lastRunUtc": "2025-01-30T14:20:00.000Z", "lastStatus": "ok", "triggerCount": 3,
      "createdUtc": "2025-01-01T08:00:00.000Z", "updatedUtc": "2025-01-01T08:00:00.000Z" }
  ]
}
```
`triggerType`: `"criticalExceeded"` (срабатывает при превышении правила из `criticalRuleId`) | `"schedule"` (периодическая проверка условия `targetType`/`targetId` + `thresholdSeconds`).
`actionType`: `"event"` (только запись события) | `"script"` (запуск `actionTarget` как процесса, требует `Workflow:AllowScriptActions=true`) | `"webhook"` (POST JSON на `actionTarget`).

### `POST /api/workflow/rules` / `PUT /api/workflow/rules/{id}` / `DELETE /api/workflow/rules/{id}`
Тело как в `GET` (без `triggerCount`, `lastStatus`, `lastRunUtc`).

### `GET /api/workflow/events?date=YYYY-MM-DD&limit=100`
```json
{
  "date": "2025-01-30",
  "events": [
    { "id": 12, "occurredUtc": "2025-01-30T14:20:00.000Z", "dayKey": "2025-01-30",
      "ruleId": 1, "ruleName": "Chrome 2 часа", "severity": "critical",
      "message": "Chrome: 125 мин из 120 мин за сутки", "targetName": "Chrome (chrome.exe)",
      "usedSeconds": 7500.0, "limitSeconds": 7200.0, "actionType": "event", "actionStatus": "ok" }
  ]
}
```

### `POST /api/workflow/run`
Тело: `{ "date": "2025-01-30" }` (необязательно; по умолчанию — сегодня).
Принудительный прогон движка.
```json
{ "ok": true, "evaluatedAt": "2025-01-30T15:00:00.000Z", "eventsCreated": 1,
  "actions": [ { "ruleName": "Алерт по превышению Chrome", "actionType": "event", "status": "ok", "message": "" } ] }
```

---

## Dashboard

### `GET /api/dashboard?date=YYYY-MM-DD` (или `?from=now-1d&to=now`)

Дополнительно к описанным ниже полям ответ всегда содержит:

```json
{
  "range": {
    "fromUtc": "2025-01-30T00:00:00.000Z", "toUtc": "2025-01-30T23:59:59.999Z",
    "fromLocal": "2025-01-30 00:00", "toLocal": "2025-01-30 23:59",
    "fromText": "2025-01-30", "toText": "2025-01-30",
    "label": "30.01.2025 (сутки)", "bucketKind": "hour", "bucketCount": 24, "totalSeconds": 21600.0
  },
  "buckets": [
    { "key": "2025-01-30T09", "label": "09:00", "startUtc": "...", "endUtc": "...",
      "isCurrent": false, "isPartial": false, "totalSeconds": 1800.0,
      "apps": [ { "appId": 3, "exeName": "chrome.exe", "displayName": "Chrome", "seconds": 1200.0 } ] }
  ],
  "appColors": { "3": "#4f8cff", "7": "#3fb27f" }
}
```

* `buckets` — столбцы графика: по часам или по суткам (см. раздел о периоде), `isPartial` отмечает
  неполные столбцы на краях периода.
* `appColors` — цвета приложений: одно и то же приложение окрашено одинаково в графике, легенде,
  ленте дня, таблице и группах.
* `hours` остаётся для совместимости и заполняется, только когда период укладывается в одни сутки.

```json
{
  "date": "2025-01-30",
  "generatedUtc": "2025-01-30T15:00:00.000Z",
  "settings": { "pollIntervalSeconds": 5, "activityIdleSeconds": 60, "monitorEnabled": true, "retentionDays": 90 },
  "totals": {
    "trackedSeconds": 21600.0, "appCount": 12, "sessionCount": 2, "userCount": 1,
    "criticalExceededCount": 1, "criticalRuleCount": 3, "monitoredAppCount": 47
  },
  "hours": [
    { "hour": 0, "totalSeconds": 0.0, "isCurrent": false, "apps": [] },
    { "hour": 9, "totalSeconds": 1800.0, "isCurrent": false,
      "apps": [ { "appId": 3, "exeName": "chrome.exe", "displayName": "Chrome", "seconds": 1200.0 } ] }
  ],
  "topApps": [
    { "appId": 3, "exeName": "chrome.exe", "displayName": "Chrome", "friendlyName": "Chrome",
      "exePath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
      "seconds": 3600.0, "percent": 54.2, "sessionCount": 1, "userNames": ["user"],
      "groupIds": [1], "groupNames": ["Браузеры"],
      "critical": { "ruleId": 1, "ruleName": "Chrome 2 часа", "limitSeconds": 7200, "usedSeconds": 3600, "percent": 50.0, "exceeded": false, "severity": "warning" } }
  ],
  "perUser": [ { "userName": "user", "seconds": 7200.0, "percent": 33.3, "sessionIds": [1] } ],
  "critical": [
    { "ruleId": 1, "ruleName": "Chrome 2 часа", "targetType": "app", "targetId": 3, "targetName": "Chrome (chrome.exe)",
      "limitSeconds": 7200, "usedSeconds": 7500.0, "percent": 104.2, "exceeded": true, "severity": "critical", "firstExceededUtc": "2025-01-30T14:20:00.000Z" }
  ],
  "events": [ { "id": 12, "occurredUtc": "2025-01-30T14:20:00.000Z", "dayKey": "2025-01-30", "ruleId": 1, "ruleName": "Chrome 2 часа",
                "severity": "critical", "message": "...", "targetName": "...", "usedSeconds": 7500.0, "limitSeconds": 7200.0,
                "actionType": "event", "actionStatus": "ok" } ],
  "timeline": [
    { "id": 120, "appId": 3, "exeName": "chrome.exe", "displayName": "Chrome", "userName": "user", "sessionId": 1,
      "startUtc": "2025-01-30T09:00:00.000Z", "endUtc": "2025-01-30T09:05:00.000Z", "seconds": 300.0, "hourIndex": 9 }
  ],
  "agents": [
    { "sessionId": 1, "userName": "user", "domain": "PC", "pid": 1234, "alive": true,
      "lastReportUtc": "2025-01-30T14:59:55.000Z", "startedUtc": "2025-01-30T07:05:00.000Z",
      "cpuPercent": 0.01, "workingSetMb": 24.5, "agentVersion": "1.0.0", "reportsTotal": 1200 }
  ]
}
```

### `GET /api/export/csv?date=YYYY-MM-DD`
`text/csv`, колонки: `Application,Path,User,Session,Start,End,Seconds,Hour`.
Заголовок ответа `Content-Disposition: attachment; filename="activity-YYYY-MM-DD.csv"`.

---

## Ingest (агент → служба)

### `POST /api/ingest/report`
```json
{
  "sessionId": 1, "userName": "user", "domain": "PC", "pid": 1234, "agentVersion": "1.0.0",
  "cpuPercent": 0.01, "workingSetMb": 24.5, "errors": ["..."],
  "samples": [
    { "appKey": "chrome.exe|c:\\program files\\google\\chrome\\application\\chrome.exe",
      "exeName": "chrome.exe", "exePath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
      "windowTitle": "Google — Chrome", "startUtc": "2025-01-30T09:14:50.000Z", "seconds": 5.0 }
  ]
}
```
Ответ:
```json
{ "ok": true, "serverTimeUtc": "2025-01-30T09:14:55.000Z",
  "config": { "pollIntervalSeconds": 5, "activityIdleSeconds": 60, "monitorEnabled": true, "collectWindowTitles": true, "heartbeatSeconds": 60 } }
```
Агент обязан применять `config` немедленно после каждого ответа.

---

## Процессы приложений и их завершение

### `GET /api/processes?appId=3`
```json
{
  "appId": 3, "displayName": "Chrome", "exeName": "chrome.exe",
  "exePath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
  "canKill": true,
  "processes": [
    { "pid": 1234, "exeName": "chrome.exe", "exePath": "C:\\...", "sessionId": 1,
      "startedUtc": "2025-01-30T08:00:00.000Z", "memoryMb": 412.7, "windowTitle": "Google — Chrome",
      "isProtected": false, "protectedReason": null }
  ]
}
```

Процессы ищутся по имени и, если путь приложения известен, сверяются по полному пути.
Системные процессы (`csrss`, `winlogon`, `services`, `lsass`, `svchost`, `dwm` и др.)
и процессы самой службы помечаются `isProtected: true` — их завершение запрещено.

### `POST /api/processes/kill`
```json
{ "appId": 3, "pid": 1234, "reason": "завершение из Web-панели" }
```
`pid` можно не указывать — тогда завершаются все процессы приложения во всех сеансах.
Требуются права на изменение (ключ администратора или запрос с loopback).

```json
{
  "ok": true, "appId": 3, "displayName": "Chrome", "exeName": "chrome.exe", "killedCount": 1,
  "results": [ { "pid": 1234, "exeName": "chrome.exe", "sessionId": 1, "status": "killed", "message": null } ]
}
```
Статусы: `killed`, `failed`, `protected`, `notFound`. Каждое завершение записывается в журнал событий
(`SourceType: "system"`, `ActionType: "killProcess"`), поэтому действие видно в ленте событий панели.

События из `GET /api/workflow/events` теперь могут иметь `sourceType`: `critical` (превышение лимита),
`workflow` (сработало правило реакции) и `system` (действия оператора, например завершение процесса).

### `GET /api/ingest/config`
Первичная конфигурация агента (тот же объект `config` + `serverTimeUtc`).
