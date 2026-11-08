import { useState } from 'react';
import { Link as RouterLink, useNavigate } from 'react-router';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Alert, Button, Link, Stack, TextField, Typography } from '@mui/material';
import { useAuth } from '../auth/AuthProvider';
import { applyFieldErrors, errorMessage } from '../api/client';
import { AuthShell } from './LoginPage';

// Mirrors the server password policy for fast feedback; the server remains the authority.
const schema = z.object({
  displayName: z.string().trim().min(1, 'Name is required').max(100),
  email: z.string().trim().email('Enter a valid email'),
  password: z
    .string()
    .min(10, 'At least 10 characters')
    .regex(/[a-z]/, 'Add a lowercase letter')
    .regex(/[A-Z]/, 'Add an uppercase letter')
    .regex(/\d/, 'Add a digit'),
});

export default function RegisterPage() {
  const { register: registerUser } = useAuth();
  const navigate = useNavigate();
  const [error, setError] = useState(null);
  const { register, handleSubmit, setError: setFieldError, formState: { errors, isSubmitting } } = useForm({
    resolver: zodResolver(schema),
    defaultValues: { displayName: '', email: '', password: '' },
  });

  const onSubmit = async (values) => {
    setError(null);
    try {
      await registerUser(values);
      navigate('/tenants', { replace: true });
    } catch (e) {
      if (!applyFieldErrors(e, setFieldError)) setError(e);
    }
  };

  return (
    <AuthShell title="Create your account" subtitle="You'll create or join a company in the next step.">
      <Stack component="form" spacing={2} onSubmit={handleSubmit(onSubmit)} noValidate>
        {error && <Alert severity="error">{errorMessage(error)}</Alert>}
        <TextField label="Full name" autoComplete="name" autoFocus {...register('displayName')} error={!!errors.displayName} helperText={errors.displayName?.message} />
        <TextField label="Work email" type="email" autoComplete="email" {...register('email')} error={!!errors.email} helperText={errors.email?.message} />
        <TextField label="Password" type="password" autoComplete="new-password" {...register('password')} error={!!errors.password} helperText={errors.password?.message ?? '10+ characters with upper, lower case and a digit'} />
        <Button type="submit" variant="contained" size="large" disabled={isSubmitting}>{isSubmitting ? 'Creating…' : 'Create account'}</Button>
        <Typography variant="body2" align="center">
          Already have an account? <Link component={RouterLink} to="/login">Sign in</Link>
        </Typography>
      </Stack>
    </AuthShell>
  );
}
