# Выпуск версий и защита релизов

Документ для владельца репозитория. В нём описано, кто может выпустить `ValDoctor.exe`,
как настроить защиту на GitHub и как проверить, что защита работает. Это закрывает пункт H1 из
[SECURITY_AUDIT.md](../SECURITY_AUDIT.md).

## Как устроен выпуск

1. Владелец ставит тег `vX.Y.Z` (или `vX.Y.Z-суффикс`) на коммит в `main`, для которого уже прошёл CI.
2. Workflow **Release** ([release.yml](../.github/workflows/release.yml)) запускается по этому тегу:
   - `Validate tag` проверяет формат тега, что тег указывает на коммит из `main` и что для этого
     коммита есть успешный запуск CI на `main`. Если хоть одно условие не выполнено, выпуск
     останавливается.
   - `build` заново собирает EXE из этого коммита, прогоняет все тесты и проверки
     ([build.yml](../.github/workflows/build.yml)).
   - `Publish GitHub Release` работает в защищённом окружении `release` и **ждёт ручного
     подтверждения** владельца. После подтверждения создаётся черновик релиза. Workflow загружает
     EXE и файл `.sha256`, скачивает их обратно и сверяет с проверенной сборкой. Только после этого
     релиз публикуется. Опубликованные релизы workflow никогда не меняет.
3. С включённой неизменяемостью релизов GitHub после публикации фиксирует тег и файлы релиза.

Права `GITHUB_TOKEN`: по умолчанию только чтение. `actions: read` есть только у проверки тега,
`contents: write` — только у шага публикации внутри окружения `release`. Секреты и ключи не нужны.

## Что должен настроить владелец

Изменить эти настройки может только владелец: у workflow и у соавторов нет прав на это.
Порядок важен. **Настройте окружение `release` (шаг 5) до слияния PR с новым release.yml.**
Если workflow сошлётся на окружение, которого ещё нет, GitHub создаст его сам и без защиты.

### 1. Доступы участников

Сейчас у `FIXAzz` есть права записи (write). В личном репозитории GitHub не позволяет выдать
соавтору права только на чтение. Соавтор всегда может делать push, а также создавать и
редактировать релизы через сайт ([документация](https://docs.github.com/en/account-and-profile/reference/permission-levels-for-a-personal-account-repository)).
Правила для тегов и окружение не мешают ему загрузить новый EXE в **уже опубликованные**
v0.1.0–v0.3.0: неизменяемость распространяется только на будущие релизы.

- **Рекомендуется:** если FIXAzz не должен выпускать версии, удалите его из соавторов:
  *Settings → Collaborators → FIXAzz → Remove*. Он сможет и дальше предлагать изменения через
  форк и pull request.
- Если доступ нужен, учтите: от его действий с существующими релизами защиты нет. Сделайте шаг 3
  с `Required approvals = 1` и разрешите себе обход только для pull request (см. примечание в шаге 3).

Перевод репозитория в организацию дал бы роли read/triage. Делать это не стоит: изменится адрес
репозитория, и проверка обновлений в уже выпущенных версиях перестанет принимать релизы.

### 2. Двухфакторная аутентификация

В личном репозитории нельзя потребовать 2FA от соавторов, поэтому включите её на своих аккаунтах сами.

- *Settings (профиля) → Password and authentication → Two-factor authentication*: включите 2FA
  через приложение-аутентификатор или ключ безопасности. Добавьте passkey и сохраните резервные коды.
- Попросите то же сделать каждого, у кого остаётся доступ на запись.
- В *Settings → Applications* и *Settings → Developer settings → Personal access tokens*
  отзовите токены и OAuth-приложения, которыми не пользуетесь.

### 3. Защита `main`

*Settings → Rules → Rulesets → New ruleset → New branch ruleset*

| Поле | Значение |
|---|---|
| Ruleset name | `main` |
| Enforcement status | Active |
| Bypass list | пусто |
| Target branches | Add target → Include default branch |
| Restrict deletions | ✓ |
| Require linear history | ✓ |
| Require a pull request before merging | ✓, Required approvals: `0`, Allowed merge methods: только Squash |
| Require status checks to pass | ✓, Require branches to be up to date before merging ✓ |
| Status checks (источник GitHub Actions) | `build / Build, test, publish (Windows x64)` и `Analyze C#` |
| Block force pushes | ✓ |

Изменения в `main`, в том числе в workflow, теперь попадают только через PR с зелёными CI и
CodeQL. В экстренном случае владелец может временно выключить ruleset (Enforcement → Disabled)
и потом включить его снова.

*Примечание:* если у соавтора остаётся write, поставьте `Required approvals: 1`. Иначе соавтор
сможет сам слить свой PR. Чтобы вы могли сливать свои PR без чужого одобрения, добавьте в Bypass
list роль **Repository admin** с режимом **For pull requests only**.

### 4. Защита тегов

*Settings → Rules → Rulesets → New ruleset → New tag ruleset*

| Поле | Значение |
|---|---|
| Ruleset name | `tags` |
| Enforcement status | Active |
| Bypass list | Add bypass → **Repository admin** → Always allow |
| Target tags | Add target → **Include all tags** |
| Restrict creations | ✓ |
| Restrict updates | ✓ |
| Restrict deletions | ✓ |
| Block force pushes | ✓ |

Почему все теги, а не только `v*`: проверка обновлений в выпущенных версиях принимает и теги без
буквы `v` (например, `9.9.9`). Правило запрещает создавать теги и соавторам, и самому
`GITHUB_TOKEN`. Поэтому workflow из чужой ветки не сможет создать тег и релиз к нему. Штатному
выпуску правило не мешает: тег ставит владелец, а workflow только использует уже существующий
тег (`--verify-tag`).

### 5. Окружение `release`

*Settings → Environments → New environment* → имя `release`:

| Поле | Значение |
|---|---|
| Required reviewers | ✓, `IskhakovMA` |
| Prevent self-review | **выключено**: вы единственный, кто ставит тег и подтверждает. С этим флагом выпуск станет невозможен |
| Wait timer | не нужен |
| Allow administrators to bypass configured protection rules | **выключено**: подтверждение нужно и администратору |
| Deployment branches and tags | Selected branches and tags → Add deployment branch or tag rule → Ref type **Tag**, шаблон `v*` |
| Environment secrets / variables | не добавлять |

Ручное подтверждение защищает от случайного или преждевременного тега: до публикации можно
посмотреть результаты сборки. От соавтора с правом записи оно не защищает: такой соавтор может
опубликовать релиз через сайт. Поэтому важен шаг 1.

### 6. Неизменяемые релизы

*Settings → General → Releases → Enable release immutability* ✓.

Опубликованный после этого релиз нельзя изменить: тег не переносится, файлы не заменяются и не
удаляются. GitHub также создаёт аттестацию релиза. Название и описание менять можно. На уже
выпущенные v0.1.0–v0.3.0 настройка не действует.

### 7. GitHub Actions

*Settings → Actions → General*:

- **Actions permissions:** «Allow IskhakovMA, and select non-IskhakovMA, actions and reusable
  workflows». Отметьте «Allow actions created by GitHub», остальное оставьте пустым. Все
  используемые actions — `actions/*` и `github/codeql-action`.
- **Require actions to be pinned to a full-length commit SHA** ✓. Все actions уже закреплены по SHA.
- **Approval for running fork pull request workflows:** «Require approval for all external contributors».
- **Workflow permissions:** «Read repository contents and packages permissions» (уже так).
  «Allow GitHub Actions to create and approve pull requests» — выключено (уже так).

### 8. Функции безопасности

*Settings → Advanced Security* (бесплатно для публичных репозиториев):

- **Secret Protection / Secret scanning** — Enable, **Push protection** — Enable.
- **Dependabot alerts** — Enable. Dependabot version updates уже настроены в `.github/dependabot.yml`.
- **Private vulnerability reporting** — Enable, чтобы об уязвимостях сообщали не в открытых issue.

## Штатный выпуск новой версии

1. Слейте PR с изменениями в `main`. Дождитесь зелёного CI на `main` для этого коммита.
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
gh api repos/IskhakovMA/riotfix/collaborators --jq '.[] | {login, role_name}'
```

Ожидается: два ruleset в статусе `active`; для `main` — правила `deletion`, `non_fast_forward`,
`required_linear_history`, `pull_request`, `required_status_checks`; в окружении есть
`required_reviewers`, `can_admins_bypass: false`, политика `v*` типа `tag`;
`{"enabled":true,...}`; `allowed_actions: "selected"`, `sha_pinning_required: true`.

**B. Workflow не может создать тег.** Создайте ветку `test/tag-protection` с одним файлом
`.github/workflows/tag-protection-test.yml`:

```yaml
name: Tag protection test
on:
  push:
    branches: [test/tag-protection]
permissions:
  contents: write
jobs:
  try:
    runs-on: ubuntu-latest
    steps:
      - name: Try to create a tag with GITHUB_TOKEN (must be rejected)
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          if gh api "repos/$GITHUB_REPOSITORY/git/refs" -f ref=refs/tags/zz-protection-test -f sha="$GITHUB_SHA"; then
            echo "::error::Tag was created: the tag ruleset is not working"; exit 1
          fi
          echo "Rejected as expected"
```

Ожидается зелёный запуск с текстом «Rejected as expected». Если запуск красный, удалите тег
`zz-protection-test`. Имя без версии, поэтому проверка обновлений его не примет. После проверки
удалите ветку. В PR её не превращайте.

**C. Прямой push в `main` запрещён.** Не пробуйте на настоящем коммите. Достаточно вывода
`rules/branches/main` из пункта A, а при следующем PR GitHub покажет, что слияние ждёт проверок.

**D. Тег не на `main` не выпускается.** Поставьте тег на коммит из ветки, которой нет в `main`:

```bash
git push origin test/tag-protection
git tag v0.0.0-guard-test origin/test/tag-protection
git push origin v0.0.0-guard-test
```

Ожидается: `Validate tag` падает с ошибкой «not on main», `build` и `Publish` не запускаются.
Затем удалите тег:

```bash
git push origin :refs/tags/v0.0.0-guard-test
git tag -d v0.0.0-guard-test
```

**E. Публикация ждёт подтверждения** (после слияния PR с новым release.yml):

```bash
git switch main && git pull --ff-only
git tag v0.3.1-rc.0
git push origin v0.3.1-rc.0
```

Ожидается: `Validate tag` и `build` зелёные, `Publish GitHub Release` в статусе «Waiting».
Нажмите **Review deployments → Reject**. Убедитесь, что релиз и черновик не появились
(`gh release list`), затем удалите тег, как в пункте D. Пока релиза нет, проверка обновлений
этот тег не видит.

## Что остаётся вне защиты

- Опубликованные v0.1.0–v0.3.0 изменяемы: их файлы может заменить любой с правом записи. Это
  закрывается только шагом 1.
- Тот, кто захватит аккаунт владельца, сможет выпустить что угодно. Отсюда шаг 2.
- Подписи кода нет. Пользователь проверяет файл по `ValDoctor.exe.sha256` и аттестации
  неизменяемого релиза.
