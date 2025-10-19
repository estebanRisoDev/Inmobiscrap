# inmobiscrap/scraping/api/auth_views.py

from rest_framework import status
from rest_framework.decorators import api_view, permission_classes
from rest_framework.permissions import AllowAny, IsAuthenticated
from rest_framework.response import Response
from django.contrib.auth import authenticate, login, logout
from django.middleware.csrf import get_token
from django.views.decorators.csrf import ensure_csrf_cookie
from rest_framework.authtoken.models import Token
from django.contrib.auth.models import User


@api_view(['GET'])
@permission_classes([AllowAny])
@ensure_csrf_cookie
def get_csrf_token(request):
    """Obtener token CSRF"""
    return Response({'csrfToken': get_token(request)})


@api_view(['POST'])
@permission_classes([AllowAny])
def login_view(request):
    """Vista de login"""
    username = request.data.get('username')
    password = request.data.get('password')
    
    if not username or not password:
        return Response(
            {'error': 'Username and password required'},
            status=status.HTTP_400_BAD_REQUEST
        )
    
    # Autenticar usuario
    user = authenticate(request, username=username, password=password)
    
    if user is not None:
        # Login con sesión de Django
        login(request, user)
        
        # IMPORTANTE: Borrar token anterior si existe y crear uno nuevo
        Token.objects.filter(user=user).delete()
        token = Token.objects.create(user=user)
        
        return Response({
            'success': True,
            'username': user.username,
            'email': user.email,
            'token': token.key,
            'is_staff': user.is_staff,
            'is_superuser': user.is_superuser,
        })
    else:
        return Response(
            {'error': 'Invalid credentials'},
            status=status.HTTP_401_UNAUTHORIZED
        )


@api_view(['POST'])
@permission_classes([IsAuthenticated])  # Cambiar a IsAuthenticated
def logout_view(request):
    """Vista de logout - Limpieza completa"""
    try:
        # Borrar token del usuario autenticado
        if hasattr(request.user, 'auth_token'):
            request.user.auth_token.delete()
        
        # Logout de la sesión de Django
        logout(request)
        
        return Response({
            'success': True,
            'message': 'Logout exitoso'
        })
    except Exception as e:
        # Si falla, igual hacer logout
        logout(request)
        return Response({
            'success': True,
            'message': 'Logout completado'
        })


@api_view(['GET'])
@permission_classes([AllowAny])
def check_auth(request):
    """Verificar si el usuario está autenticado"""
    if request.user.is_authenticated:
        # Verificar si tiene token
        try:
            token = Token.objects.get(user=request.user)
            has_token = True
        except Token.DoesNotExist:
            has_token = False
            
        return Response({
            'authenticated': True,
            'username': request.user.username,
            'is_staff': request.user.is_staff,
            'has_token': has_token
        })
    else:
        return Response({
            'authenticated': False
        })