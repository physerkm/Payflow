import { useState } from 'react';
import { Link as RouterLink, useLocation, useNavigate } from 'react-router';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Alert, Box, Button, Card, CardContent, Link, Stack, TextField, Typography } from '@mui/material';
import { useAuth } from '../auth/AuthProvider';
import { errorMessage } from '../api/client';

const schema = z.object({
  email: z.string().trim().min(1, 'Email is required').email('Enter a valid email'),
  password: z.string().min(1, 'Password is required'),
});

export function AuthShell({ title, subtitle, children }) {
  return (
    <Box sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', p: 2, bgcolor: 'background.default' }}>
      <Stack spacing={3} sx={{ width: '100%', maxWidth: 420 }}>
        <Stack direction="row" spacing={1.25} alignItems="center" justifyContent="center">
          <Box component="img" src="/favicon.svg" alt="" sx={{ width: 36, height: 36 }} />
          <Typography variant="h5" fontWeight={800}>PayFlow</Typography>
        </Stack>
        <Card>
          <CardContent sx={{ p: 4 }}>
            <Typography variant="h6" component="h1">{title}</Typography>
            {subtitle && <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, mb: 3 }}>{subtitle}</Typography>}
            {children}
          </CardContent>
        </Card>
        <Typography variant="caption" color="text.secondary" align="center">
          Portfolio project · simulated payment provider · no real card data
        </Typography>
      </Stack>
    </Box>
  );
}

export default function LoginPage() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [error, setError] = useState(null);
  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm({ resolver: zodResolver(schema), defaultValues: { email: '', password: '' } });

  const onSubmit = async (values) => {
    setError(null);
    try {
      const tenant = await login(values);
      navigate(tenant ? location.state?.from ?? '/' : '/tenants', { replace: true });
    } catch (e) {
      setError(e);
    }
  };

  return (
    <AuthShell title="Sign in" subtitle="Manage orders, payments and refunds for your company.">
      <Stack component="form" spacing={2} onSubmit={handleSubmit(onSubmit)} noValidate>
        {error && <Alert severity="error">{errorMessage(error)}</Alert>}
        <TextField label="Email" type="email" autoComplete="email" autoFocus {...register('email')} error={!!errors.email} helperText={errors.email?.message} />
        <TextField label="Password" type="password" autoComplete="current-password" {...register('password')} error={!!errors.password} helperText={errors.password?.message} />
        <Button type="submit" variant="contained" size="large" disabled={isSubmitting}>{isSubmitting ? 'Signing in…' : 'Sign in'}</Button>
        <Typography variant="body2" align="center">
          New to PayFlow? <Link component={RouterLink} to="/register">Create an account</Link>
        </Typography>
      </Stack>
    </AuthShell>
  );
}
