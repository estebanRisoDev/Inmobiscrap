import os
from celery import Celery
from celery.schedules import crontab

# Configurar el módulo de settings de Django
os.environ.setdefault('DJANGO_SETTINGS_MODULE', 'inmobiscrap.settings')

app = Celery('inmobiscrap')

# Usar string para configurar el broker, permite pickling del objeto
app.config_from_object('django.conf:settings', namespace='CELERY')

# Cargar tareas de todos los módulos de Django registrados
app.autodiscover_tasks()

# Configurar tareas periódicas con Celery Beat
app.conf.beat_schedule = {
    # Scrapear URLs pendientes cada 6 horas
    'scrape-pending-urls-every-6-hours': {
        'task': 'inmobiscrap.scraping.tasks.scrape_pending_urls',
        'schedule': crontab(minute=0, hour='*/6'),  # Cada 6 horas
    },
    
    # Scrapeo diario a las 2 AM
    'daily-scraping-2am': {
        'task': 'inmobiscrap.scraping.tasks.scrape_pending_urls',
        'schedule': crontab(hour=2, minute=0),  # Todos los días a las 2 AM
    },
    
    # Scrapeo diario a las 2 PM
    'daily-scraping-2pm': {
        'task': 'inmobiscrap.scraping.tasks.scrape_pending_urls',
        'schedule': crontab(hour=14, minute=0),  # Todos los días a las 2 PM
    },
    
    # Limpieza de logs antiguos cada semana (domingos a las 3 AM)
    'cleanup-old-logs-weekly': {
        'task': 'inmobiscrap.scraping.tasks.cleanup_old_logs',
        'schedule': crontab(hour=3, minute=0, day_of_week=0),  # Domingos a las 3 AM
    },
    
    # Desactivar URLs fallidas cada día a las 4 AM
    'deactivate-failed-urls-daily': {
        'task': 'inmobiscrap.scraping.tasks.deactivate_failed_urls',
        'schedule': crontab(hour=4, minute=0),  # Todos los días a las 4 AM
    },
}

# Timezone para las tareas
app.conf.timezone = 'America/Santiago'

@app.task(bind=True)
def debug_task(self):
    """Tarea de debug para probar Celery"""
    print(f'Request: {self.request!r}')