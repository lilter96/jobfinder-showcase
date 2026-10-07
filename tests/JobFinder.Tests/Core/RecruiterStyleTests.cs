using JobFinder.Core.Filtering;

namespace JobFinder.Tests.Core;

public sealed class RecruiterStyleTests
{
    /// <summary>The message that actually went out on 2026-09-28: a resume in a message.</summary>
    private const string ResumeInAMessage =
        "Юлия, здравствуйте! Нашёл вакансию .NET-разработчика True.Code на hh.ru и хочу, чтобы меня рассмотрели. У меня более 5 лет коммерческого " +
        "опыта в backend-разработке на C#/.NET. Разрабатывал микросервисы и интеграции для e-commerce-платформы с более чем 50 тыс. заказов в сутки. " +
        "С помощью Transactional Outbox и Inbox обеспечил доставку событий на уровне 99,99%. В игровом backend вынес транзакционные события в Kafka " +
        "и снизил p99 операций до 50 мс. Также работал с RabbitMQ, Redis, CQRS, тестами и Docker. Рассматриваю удалённую работу из Минска. " +
        "Резюме в PDF приложил. Буду рад, если посмотрите его и предложите коротко обсудить задачи.";

    [Fact]
    public void A_resume_in_a_message_is_caught_for_every_reason()
    {
        var problems = RecruiterStyle.Problems(ResumeInAMessage, first: true);
        Assert.Contains(problems, p => p.Contains("нашел вакансию"));
        Assert.Contains(problems, p => p.Contains("хвост со стеком"));
        Assert.Contains(problems, p => p.Contains("технологий"));
        Assert.Contains(problems, p => p.Contains("цифр (3)"));
    }

    /// <summary>The cover letter criticized on 2026-09-28: a compressed resume with AI phrases.</summary>
    [Fact]
    public void A_compressed_resume_letter_is_caught()
    {
        var problems = RecruiterStyle.Problems(
            "Здравствуйте! Мне интересна роль Full-stack разработчика .NET + AI-агенты: мой опыт хорошо совпадает с задачами продукта. Более 5 лет " +
            "разрабатываю высоконагруженные микросервисы на Kafka и EF Core, обеспечил доставку событий 99,99%, SLA менее 120 мс и p99 менее 50 мс. " +
            "Работал с React, OpenTelemetry, Prometheus и Grafana. Готов созвониться и обсудить, как мой опыт применим к вашим задачам.",
            first: true, maxMetrics: 1);
        Assert.Contains(problems, p => p.Contains("AI-фраза"));
        Assert.Contains(problems, p => p.Contains("технологий"));
        Assert.Contains(problems, p => p.Contains("цифр"));
    }

    [Fact]
    public void A_letter_with_a_hook_and_one_example_passes() =>
        Assert.Empty(RecruiterStyle.Problems(
            "Здравствуйте! Заинтересовала вакансия из-за сочетания .NET и работы с внешними интеграциями. Основной мой опыт - backend на C#, и ближе " +
            "всего к вашим задачам проект, где я сделал так, что события между сервисами перестали теряться при сбоях. Есть и опыт с React, поэтому " +
            "fullstack-часть роли мне тоже интересна. Буду рад обсудить задачи и рассказать подробнее о проектах.",
            first: true, maxMetrics: 1));

    [Fact]
    public void A_personal_message_with_one_fact_passes() =>
        Assert.Empty(RecruiterStyle.Problems(
            "Юлия, здравствуйте! Увидел, что True.Code автоматизирует производства, и в вакансии много про брокеры сообщений и микросервисы. " +
            "Последние годы я как раз строю такой backend на .NET: на e-commerce платформе сделал так, что события между сервисами перестали " +
            "теряться при сбоях, и поддержка забыла про ручные сверки заказов. Работаю удалённо из Минска. Если профиль подходит, удобно созвониться на этой неделе.",
            first: true));

    [Theory]
    [InlineData("Спасибо! Да, во вторник после 14:00 удобно.", false, 0)]
    [InlineData("Здравствуйте!", true, 1)]
    public void A_reply_in_a_conversation_may_be_short_a_first_word_may_not(string text, bool first, int expected) =>
        Assert.Equal(expected, RecruiterStyle.Problems(text, first).Count);

    [Theory]
    [InlineData("мне близко сочетание серверной разработки и интерфейсной части, а тематика платежей тоже знакома", "подгонка")]
    [InlineData("Мой опыт соответствует вашим требованиям, интересен ваш стек.", "подгонка")]
    [InlineData("Юлия, удалось посмотреть мои сообщения по вакансии True.Code?", "проверка")]
    public void Fitting_in_general_words_and_audit_reminders_are_caught(string text, string expected) =>
        Assert.Contains(RecruiterStyle.Problems(text, first: false), p => p.Contains(expected));

    [Fact]
    public void One_supporting_item_of_the_same_case_is_not_a_stack_tail() =>
        Assert.DoesNotContain(RecruiterStyle.Problems(
            "Здравствуйте! В вакансии важно совмещать .NET-бэкенд и React-фронтенд. На e-commerce платформе я занимался надёжной доставкой событий " +
            "и синхронизацией остатков, а также работал с frontend на React/TypeScript. Сейчас я в Минске, удалённый формат подходит. Буду рад обсудить вакансию.",
            first: true), p => p.Contains("хвост"));

    [Fact]
    public void A_polite_reminder_passes() =>
        Assert.Empty(RecruiterStyle.Problems("Юлия, добрый день! Хотел уточнить, актуальна ли ещё вакансия. Буду рад обратной связи.", first: false));

    [Fact]
    public void Numbers_that_are_not_metrics_do_not_count() =>
        Assert.DoesNotContain(RecruiterStyle.Problems(
            "Анна, здравствуйте! В команде из 5 разработчиков отвечал за интеграции, 3 года вёл обмен заказами с перевозчиками, и расхождения " +
            "в статусах перестали всплывать у поддержки. Сейчас ищу похожие задачи на .NET, работаю удалённо из Минска. Буду рад обсудить вакансию.",
            first: true), p => p.Contains("цифр"));
}
