# Сквозные тесты IMAP/SMTP

`ImapIntegrationTests` работают с настоящим IMAP-сервером (Dovecot) и SMTP-сервером.
Без переменной `MAILCLIENT_IMAP_TEST=1` они помечаются как пропущенные (skipped).

Тестовое окружение (Ubuntu):

```bash
apt-get install -y dovecot-imapd dovecot-managesieved dovecot-sieve && pip install aiosmtpd
# Dovecot: пользователь ivanov@test.ru с паролем на кириллице, TLS на порту 11993
# с сертификатом, подписанным тестовым корневым УЦ (ca.crt) — проверяется импорт корневого сертификата.
python3 -m aiosmtpd -n -l 127.0.0.1:2525 -c aiosmtpd.handlers.Mailbox /srv/mc-imaptest/smtp
```

Запуск:

```bash
MAILCLIENT_IMAP_TEST=1 IMAP_TEST_HOST=localhost IMAP_TEST_PORT=11993 \
IMAP_TEST_CA=/srv/mc-imaptest/ca.crt IMAP_TEST_USER=ivanov@test.ru IMAP_TEST_PASSWORD='Пароль-123' \
SMTP_TEST_PORT=2525 SMTP_TEST_MAILDIR=/srv/mc-imaptest/smtp SIEVE_TEST_PORT=14190 \
dotnet test --project tests/MailClient.Tests
```

`SIEVE_TEST_PORT` включает проверку правил пересылки через ManageSieve (STARTTLS) на том же Dovecot.

Сгенерированные Sieve-скрипты дополнительно прогоняются через `sieve-test` (Pigeonhole) на настоящих
письмах, если задать `SIEVE_TEST_CONFIG` — путь к конфигурации Dovecot для `sieve-test`:

```
mail_uid=nobody
mail_gid=nogroup
first_valid_uid=0
mail_location=maildir:/tmp/stmail
```

Пример конфигурации Dovecot — в `dovecot.example.conf` рядом с этим файлом.
