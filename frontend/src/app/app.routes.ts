import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/auth.service';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [guestGuard],
    title: 'Sign in · Paperdesk',
    data: { mode: 'login' },
    loadComponent: () => import('./pages/auth.page').then((m) => m.AuthPage),
  },
  {
    path: 'register',
    canActivate: [guestGuard],
    title: 'Create account · Paperdesk',
    data: { mode: 'register' },
    loadComponent: () => import('./pages/auth.page').then((m) => m.AuthPage),
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./shell.component').then((m) => m.ShellComponent),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'home' },
      {
        path: 'home',
        title: 'Home · Paperdesk',
        loadComponent: () => import('./pages/home.page').then((m) => m.HomePage),
      },
      {
        path: 'portfolio',
        title: 'Portfolio · Paperdesk',
        loadComponent: () => import('./pages/portfolio.page').then((m) => m.PortfolioPage),
      },
      {
        path: 'trade/:symbol',
        title: 'Trade · Paperdesk',
        loadComponent: () => import('./pages/trade.page').then((m) => m.TradePage),
      },
      {
        path: 'creators',
        title: 'Creators · Paperdesk',
        loadComponent: () => import('./pages/creators.page').then((m) => m.CreatorsPage),
      },
      {
        path: 'creators/:id',
        title: 'Creator · Paperdesk',
        loadComponent: () => import('./pages/creator-detail.page').then((m) => m.CreatorDetailPage),
      },
      {
        path: 'digest',
        title: 'Digest · Paperdesk',
        loadComponent: () => import('./pages/digest.page').then((m) => m.DigestPage),
      },
      {
        path: 'watchlist',
        title: 'Lists · Paperdesk',
        loadComponent: () => import('./pages/watchlist.page').then((m) => m.WatchlistPage),
      },
      {
        path: 'orders',
        title: 'Orders · Paperdesk',
        loadComponent: () => import('./pages/orders.page').then((m) => m.OrdersPage),
      },
      {
        path: 'account',
        title: 'Account · Paperdesk',
        loadComponent: () => import('./pages/account.page').then((m) => m.AccountPage),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
