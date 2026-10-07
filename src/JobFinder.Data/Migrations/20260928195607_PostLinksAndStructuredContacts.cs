using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <summary>
    /// raw_posts.links: the links a Telegram post's text does not show. vacancies.contacts: one text line per contact → a typed jsonb list
    /// (kind, normalized value) in the lines' order. A legacy line that is no contact at all is dropped; re-reading the post (/reparse)
    /// refills the list with labels and sources.
    /// </summary>
    public partial class PostLinksAndStructuredContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "links",
                table: "raw_posts",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.Sql("""
                ALTER TABLE vacancies ADD COLUMN contacts_list jsonb NOT NULL DEFAULT '[]'::jsonb;

                UPDATE vacancies v SET contacts_list = coalesce((
                    SELECT jsonb_agg(jsonb_build_object('kind', c.kind, 'value', c.value) ORDER BY c.ord)
                    FROM (
                        SELECT s.ord,
                            CASE
                                WHEN s.l ~* '^[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}$' THEN 'Email'
                                WHEN s.l ~* '^@[a-z][a-z0-9_]{3,31}$' THEN 'Telegram'
                                WHEN s.l ~* '^(https?://)?(www\.)?(t|telegram)\.me/[a-z][a-z0-9_]{3,31}/?$' THEN 'Telegram'
                                WHEN s.l ~* 'linkedin\.com/in/' THEN 'LinkedIn'
                                WHEN s.l ~ '^\+?[0-9][0-9 ()\-]{7,}[0-9]$' THEN 'Phone'
                                WHEN s.l ~* '^https?://' THEN 'Link'
                            END AS kind,
                            CASE
                                WHEN s.l ~* '^[a-z0-9._%+-]+@' OR s.l ~* '^@' THEN lower(s.l)
                                WHEN s.l ~* '^(https?://)?(www\.)?(t|telegram)\.me/'
                                    THEN '@' || lower(regexp_replace(s.l, '^(https?://)?(www\.)?(t|telegram)\.me/([A-Za-z0-9_]+)/?$', '\4', 'i'))
                                WHEN s.l ~ '^\+?[0-9][0-9 ()\-]{7,}[0-9]$' THEN regexp_replace(s.l, '[^0-9+]', '', 'g')
                                ELSE s.l
                            END AS value
                        FROM (SELECT btrim(t.x) AS l, t.ord FROM regexp_split_to_table(v.contacts, E'\n') WITH ORDINALITY AS t(x, ord)) s
                        WHERE s.l <> ''
                    ) c
                    WHERE c.kind IS NOT NULL), '[]'::jsonb)
                WHERE v.contacts IS NOT NULL;

                ALTER TABLE vacancies DROP COLUMN contacts;
                ALTER TABLE vacancies RENAME COLUMN contacts_list TO contacts;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "links",
                table: "raw_posts");

            migrationBuilder.Sql("""
                ALTER TABLE vacancies ADD COLUMN contacts_text text;
                UPDATE vacancies v SET contacts_text = (
                    SELECT string_agg(e.item ->> 'value', E'\n' ORDER BY e.ord)
                    FROM jsonb_array_elements(v.contacts) WITH ORDINALITY AS e(item, ord))
                WHERE jsonb_array_length(v.contacts) > 0;
                ALTER TABLE vacancies DROP COLUMN contacts;
                ALTER TABLE vacancies RENAME COLUMN contacts_text TO contacts;
                """);
        }
    }
}
