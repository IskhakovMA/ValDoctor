# Выпуск версий и защита релизов

Кто может выпустить `ValDoctor.exe`, какие настройки GitHub это обеспечивают и как проверить, что
защита работает. Закрывает пункт H1 из [SECURITY_AUDIT.md](../SECURITY_AUDIT.md).

## Как устроен выпуск

1. Владелец ставит тег `vX.Y.Z` (или `vX.Y.Z-суффикс`) на коммит в `main`, для которого уже прошёл CI.
2. Workflow **Release** ([release.yml](../.github/workflows/release.yml)) запускается по этому тегу:
   - `Validate tag` проверяет формат тега, что тег указывает на коммит из `main` и что для этого
     коммита есть успешный запуск CI на `main`. Если хоть одно условие не выполнено, выпуск
     останавливается.
   - `build` заново собирает EXE из этого коммита и прогоняет все тесты и проверки
     ([build.yml](../.github/workflows/build.yml)).
   - `Publish GitHub Release` работает в защищённом окружении `release` и **ждёт ручного
     подтверждения** владельца. После подтверждения создаётся черновик релиза. Workflow загружает
     EXE и `.sha256`, скачивает их обратно и сверяет со сборкой. Только после этого релиз
     публикуется. Опубликованные релизы workflow никогда не меняет.
3. Неизменяемость релизов включена: после публикации GitHub фиксирует тег и файлы релиза и
   создаёт аттестацию.

Права `GITHUB_TOKEN`: по умолчанию только чтение. `actions: read` есть только у проверки тега,
`contents: write` — только у шага публикации внутри окружения `release`. Секреты и ключи не нужны.

## Настройки репозитория

| Настройка | Значение |
|---|---|
| Ruleset `protect main` (ветка по умолчанию) | только через PR, 1 одобрение (не от автора последнего push), обсуждения закрыты, только squash, линейная история, проверки `build / Build, test, publish (Windows x64)` и `Analyze C#` с актуальной веткой, запрет force push и удаления, без исключений |
| Ruleset `protect tags` (все теги, `~ALL`) | запрет создания, изменения, удаления и force push. Исключение одно: роль Repository admin (владелец) |
| Окружение `release` | обязательный проверяющий `IskhakovMA`, «Prevent self-review» выключен, обхода для администраторов нет, развёртывание только с тегов `v*`, секретов нет |
| Неизменяемые релизы | включены для новых релизов |
| GitHub Actions | разрешены только actions от GitHub и этого аккаунта, обязательное закрепление по SHA, `GITHUB_TOKEN` по умолчанию только читает, workflow не одобряют PR, workflow из форков требуют одобрения для всех внешних участников |
| Secret scanning, push protection | включены |
| Dependabot alerts и security updates | включены |
| Private vulnerability reporting | включено |
| Соавторы | `IskhakovMA` (admin), `FIXAzz` (write, доверенный соразработчик) |

Правило для тегов охватывает все теги, а не только `v*`: проверка обновлений в выпущенных версиях
принимает и теги без `v` (например, `9.9.9`). Правило не даёт создать тег ни соразработчику, ни
`GITHUB_TOKEN`. Поэтому workflow из любой ветки не сможет создать тег и релиз к нему. Штатному
выпуску правило не мешает: тег ставит владелец, а workflow использует уже существующий тег
(`--verify-tag`).

«Prevent self-review» выключен намеренно: тег ставит и выпуск подтверждает один и тот же человек.
С включённым флагом выпуск станет невозможен.

## Принятый остаточный риск

- В личном репозитории роли read/triage недоступны. Соавтор с правом записи, в том числе FIXAzz,
  может через сайт создать или изменить релиз для существующего тега. Неизменяемость не
  распространяется на релизы до её включения, то есть на v0.1.0–v0.3.0. Риск принят, потому что
  FIXAzz — доверенный соразработчик. Главная защита здесь — 2FA на обоих аккаунтах.
- Тот, кто захватит аккаунт владельца, сможет выпустить что угодно. Нужна 2FA, лучше с ключом
  безопасности или passkey.
- Подписи кода нет. Пользователь проверяет файл по `ValDoctor.exe.sha256` и аттестации
  неизменяемого релиза.
- Переименовывать репозиторий нельзя. RiotFix 0.2.x принимает только ссылки на релизы
  `https://github.com/IskhakovMA/riotfix/releases/tag/...`, ValDoctor 0.3.0 — `riotfix` или
  `valdoctor` строчными, с точным сравнением. После переименования, например в `ValDoctor`,
  установленные версии перестанут видеть обновления.

## Штатный выпуск новой версии

1. Слейте PR в `main` после одобрения. Дождитесь зелёного CI на `main` для этого коммита.
2. Поставьте и отправьте тег:
   ```bash
   git switch main && git pull --ff-only
   git tag -a v0.3.1 -m "ValDoctor v0.3.1"
   git push origin v0.3.1
   ```
3. *Actions → Release → запуск по тегу*: дождитесь, пока `Validate tag` и `build` станут зелёными.
4. Нажмите **Review deployments → release → Approve and deploy**.
5. Проверьте релиз: два файла, `ValDoctor.exe` и `ValDoctor.exe.sha256`. В журнале шага
   `Publish release` должно быть `immutable: true`.

Если `Validate tag` упал: тег стоит не на `main` или CI на `main` для этого коммита ещё не
прошёл. Удалите тег (`git push origin :refs/tags/v0.3.1` и `git tag -d v0.3.1`), исправьте
причину и поставьте тег снова. Если вы отклонили подтверждение, релиз не создаётся, тег можно
удалить так же.

## Проверка, что защита работает

Проверки ничего не публикуют и не трогают существующие релизы.

**A. Настройки на месте** (только чтение):

```bash
gh api repos/IskhakovMA/riotfix/rulesets --jq '.[] | {name, target, enforcement}'
gh api repos/IskhakovMA/riotfix/rules/branches/main --jq '.[].type'
gh api repos/IskhakovMA/riotfix/environments/release --jq '{can_admins_bypass, protection_rules, deployment_branch_policy}'
gh api repos/IskhakovMA/riotfix/environments/release/deployment-branch-policies --jq '.branch_policies[] | {name, type}'
gh api repos/IskhakovMA/riotfix/immutable-releases
gh api repos/IskhakovMA/riotfix/actions/permissions
gh api repos/IskhakovMA/riotfix --jq '{full_name, security_and_analysis}'
```

**B. Тег не может создать никто, кроме владельца.** Соразработчик пробует отправить тег:

```bash
git tag zz-protection-test && git push origin zz-protection-test
```

Ожидается отказ `GH013: Repository rule violations`. Затем удалите локальный тег:
`git tag -d zz-protection-test`. Если push прошёл, владелец удаляет тег на GitHub. Имя без
версии, поэтому проверка обновлений его не примет.

**C. Тег не на `main` не выпускается** (выполняет владелец):

```bash
git switch -c test/release-guard origin/main
git commit --allow-empty -m "test: release guard"
git push origin test/release-guard
git tag v0.0.0-guard-test && git push origin v0.0.0-guard-test
```

Ожидается: `Validate tag` падает с ошибкой «not on main», `build` и `Publish` не запускаются.
Затем удалите тег и ветку:

```bash
git push origin :refs/tags/v0.0.0-guard-test :test/release-guard
git tag -d v0.0.0-guard-test
```

**D. Публикация ждёт подтверждения** (после слияния PR с новым release.yml):

```bash
git switch main && git pull --ff-only
git tag v0.3.1-rc.0 && git push origin v0.3.1-rc.0
```

Ожидается: `Validate tag` и `build` зелёные, `Publish GitHub Release` в статусе «Waiting».
Нажмите **Review deployments → Reject**. Убедитесь, что релиз и черновик не появились
(`gh release list`), затем удалите тег, как в пункте C. Пока релиза нет, проверка обновлений
этот тег не видит.
