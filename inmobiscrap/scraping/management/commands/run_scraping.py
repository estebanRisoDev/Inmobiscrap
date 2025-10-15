from django.core.management.base import BaseCommand, CommandError
from django.utils import timezone
from scraping.models import URLToScrape
from scraping.tasks import scrape_url_task, scrape_pending_urls
import sys


class Command(BaseCommand):
    help = 'Ejecutar scraping de URLs'

    def add_arguments(self, parser):
        parser.add_argument(
            '--url-id',
            type=int,
            help='ID de la URL específica a scrapear'
        )
        parser.add_argument(
            '--all-pending',
            action='store_true',
            help='Scrapear todas las URLs pendientes'
        )
        parser.add_argument(
            '--site',
            type=str,
            help='Scrapear todas las URLs de un sitio específico'
        )
        parser.add_argument(
            '--sync',
            action='store_true',
            help='Ejecutar sincrónicamente (sin Celery)'
        )

    def handle(self, *args, **options):
        url_id = options.get('url_id')
        all_pending = options.get('all_pending')
        site = options.get('site')
        sync = options.get('sync')

        if not any([url_id, all_pending, site]):
            raise CommandError('Debes especificar --url-id, --all-pending, o --site')

        # Scrapear URL específica
        if url_id:
            try:
                url_obj = URLToScrape.objects.get(id=url_id)
            except URLToScrape.DoesNotExist:
                raise CommandError(f'URL con ID {url_id} no existe')

            self.stdout.write(f'Scrapeando URL: {url_obj.url}')
            
            if sync:
                # Ejecutar sincrónicamente
                result = scrape_url_task(url_id)
                self.stdout.write(self.style.SUCCESS(f'Resultado: {result}'))
            else:
                # Ejecutar con Celery
                task = scrape_url_task.delay(url_id)
                self.stdout.write(self.style.SUCCESS(f'Tarea iniciada: {task.id}'))

        # Scrapear todas las pendientes
        elif all_pending:
            self.stdout.write('Scrapeando todas las URLs pendientes...')
            
            if sync:
                now = timezone.now()
                urls = URLToScrape.objects.filter(
                    is_active=True,
                    status__in=['pending', 'failed']
                )
                
                total = urls.count()
                self.stdout.write(f'Encontradas {total} URLs para scrapear')
                
                for i, url_obj in enumerate(urls, 1):
                    self.stdout.write(f'[{i}/{total}] Scrapeando: {url_obj.url}')
                    try:
                        result = scrape_url_task(url_obj.id)
                        self.stdout.write(self.style.SUCCESS(f'  ✓ {result}'))
                    except Exception as e:
                        self.stdout.write(self.style.ERROR(f'  ✗ Error: {e}'))
            else:
                result = scrape_pending_urls.delay()
                self.stdout.write(self.style.SUCCESS(f'Tarea iniciada: {result.id}'))

        # Scrapear por sitio
        elif site:
            urls = URLToScrape.objects.filter(
                site_name__icontains=site,
                is_active=True
            )
            
            if not urls.exists():
                raise CommandError(f'No se encontraron URLs activas para el sitio: {site}')

            total = urls.count()
            self.stdout.write(f'Encontradas {total} URLs para el sitio "{site}"')
            
            for i, url_obj in enumerate(urls, 1):
                self.stdout.write(f'[{i}/{total}] Scrapeando: {url_obj.url}')
                
                if sync:
                    try:
                        result = scrape_url_task(url_obj.id)
                        self.stdout.write(self.style.SUCCESS(f'  ✓ {result}'))
                    except Exception as e:
                        self.stdout.write(self.style.ERROR(f'  ✗ Error: {e}'))
                else:
                    task = scrape_url_task.delay(url_obj.id)
                    self.stdout.write(self.style.SUCCESS(f'  Tarea iniciada: {task.id}'))

        self.stdout.write(self.style.SUCCESS('\n✅ Proceso completado'))