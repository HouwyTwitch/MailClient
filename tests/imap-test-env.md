# Сквозные тесты IMAP/SMTP

`ImapIntegrationTests` работают с настоящим IMAP-сервером (Dovecot) и SMTP-сервером.
Без переменной `MAILCLIENT_IMAP_TEST=1` они ничего не проверяют и сразу завершаются успешно.

Тестовое окружение (Ubuntu):

```bash
apt-get install -y dovecot-imapd && pip install aiosmtpd
# Dovecot: пользователь ivanov@test.ru с паролем на кириллице, TLS на порту 11993
# с сертификатом, подписанным тестовым корневым УЦ (ca.crt) — проверяется импорт корневого сертификата.
python3 -m aiosmtpd -n -l 127.0.0.1:2525 -c aiosmtpd.handlers.Mailbox /srv/mc-imaptest/smtp
```

Запуск:

```bash
MAILCLIENT_IMAP_TEST=1 IMAP_TEST_HOST=localhost IMAP_TEST_PORT=11993 \
IMAP_TEST_CA=/srv/mc-imaptest/ca.crt IMAP_TEST_USER=ivanov@test.ru IMAP_TEST_PASSWORD='Пароль-123' \
SMTP_TEST_PORT=2525 SMTP_TEST_MAILDIR=/srv/mc-imaptest/smtp \
dotnet test tests/MailClient.Tests --filter ImapIntegrationTests
```

Пример конфигурации Dovecot — в `dovecot.example.conf` рядом с этим файлом.
