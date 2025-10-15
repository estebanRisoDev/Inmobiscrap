from django.apps import AppConfig


class ScrapingConfig(AppConfig):
    default_auto_field = 'django.db.models.BigAutoField'
    name = 'scraping'
    verbose_name = 'Sistema de Scraping'
    
    def ready(self):
        """
        Código que se ejecuta cuando la app está lista
        """
        # Importar señales si las necesitas en el futuro
        # import scraping.signals
        pass